#r "nuget: YamlDotNet"
#r "nuget: Fli"
#r "nuget: Spectre.Console"

#load "..\\Ralph.fsx"

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Xml.Linq
open Utils
open Prompting
open Recovery

let check condition message =
    if not condition then
        failwith message

let directory = Environment.GetEnvironmentVariable "RECOVERY_PROMPT_DIRECTORY"
let closed = Environment.GetEnvironmentVariable "RECOVERY_PROMPT_MODE" = "closed"
let utf8 = UTF8Encoding(false)

let sprintPath =
    match Environment.GetEnvironmentVariable "RECOVERY_SPRINT_PATH" with
    | null ->
        let path = Path.Combine(directory, "00_Prompt.md")

        File.WriteAllText(
            path,
            """# Sprint: prompt transport

### Validation scope and completion contract
dotnet fsi tests\Recovery.Tests.fsx
dotnet test tests\Shared.Tests\DailyMonitor.Shared.Tests.fsproj -c Debug --filter 'FullyQualifiedName~ExecutorStateTests'
git diff --check

## Definition of Done
- Preserve multiline "quoted" input exactly.
"""
        )

        path
    | path -> path

let text = (File.ReadAllText sprintPath).Replace("\r\n", "\n")
let heading = "### Validation scope and completion contract\n"
let start = text.IndexOf(heading, StringComparison.Ordinal)
check (start >= 0) "On-disk H3 validation section missing"

let finish =
    text.IndexOf("\n## Definition of Done", start, StringComparison.Ordinal)

check (finish > start) "On-disk validation section end missing"

let contract =
    text.Substring(start + heading.Length, finish - start - heading.Length).Trim()

let sprint = SprintFiles.readSprint sprintPath |> Option.get
check (validationContract sprint = contract) "On-disk validation commands changed"

let payload =
    String.replicate (if closed then 131072 else 8192) "multiline \"double\" and 'single' quotes \u03bb\r\nnext line\n"

let prompts =
    [ "Implement-0", Prompts.implement sprint 1 [ payload ] [] [] [] [], "SUBTASK_COMPLETE", None
      "Verify-FUNCTIONAL",
      verifierPreamble
      + Verifiers.getPrompt "FUNCTIONAL"
      + buildSprintVerificationContext sprint Map.empty None "FUNCTIONAL"
      + payload
      + verifierSuffix,
      "VERIFY_PASSED",
      Some "saved-prompt-session" ]

let xml = prompts.Head |> fun (_, prompt, _, _) -> XElement.Parse prompt

check
    ((xml.Descendants(XName.Get "validation_contract") |> Seq.exactlyOne).Value = contract)
    "Implementer lost the complete on-disk contract"

let _, verifier, _, _ = prompts[1]

check
    (verifier.Contains("=== VALIDATION SCOPE AND COMPLETION CONTRACT ===\n" + contract + "\n"))
    "Verifier lost the complete on-disk contract"

for title, prompt, token, resume in (if closed then [ prompts.Head ] else prompts) do
    let expected = utf8.GetBytes(prompt + Environment.NewLine)
    check (expected.Length > 65536) "Prompt did not exceed a pipe buffer"
    File.WriteAllBytes(Path.Combine(directory, "expected.bin"), expected)
    Environment.SetEnvironmentVariable("RECOVERY_PROMPT_TOKEN", token)
    let mutable failure = None
    let mutable returned = None

    try
        returned <- Some(Ralph.runAgent prompt title false resume |> Async.RunSynchronously)
    with ex ->
        failure <- Some ex

    let invocation =
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "invocation.json")))

    let arguments =
        invocation["Arguments"].AsArray()
        |> Seq.map (fun n -> n.GetValue<string>())
        |> Seq.toArray

    check (invocation["Directory"].GetValue<string>() = Config.workDir) "Agent working directory changed"

    let sessionArgs =
        match resume with
        | Some session -> [| "--resume=" + session |]
        | None ->
            let index = Array.findIndex ((=) "--name") arguments
            let name = arguments[index + 1]
            check (name.StartsWith(title + "-", StringComparison.Ordinal)) "Fresh session title changed"
            check (name.Length = title.Length + 9) "Fresh session suffix changed"
            [| "--name"; name |]

    let expectedArgs =
        Array.append
            [| "--allow-all-tools"
               "--allow-all-paths"
               "--no-ask-user"
               "--no-color"
               "--plain-diff"
               "-s"
               "--model"
               Config.Model
               "--effort"
               Config.Effort
               "--context"
               Config.Context
               "--stream"
               "off" |]
            sessionArgs

    check (arguments = expectedArgs) "Session arguments differ from the production invocation contract"

    let receipts =
        Directory.GetFiles(Path.Combine(Config.ralphDir, "captures"), "*.result.json")

    let receipt =
        receipts
        |> Array.map (fun path -> JsonNode.Parse(File.ReadAllText path))
        |> Array.find (fun node -> node["Phase"].GetValue<string>() = title)

    check
        (receipt["StreamsDrained"].GetValue<bool>()
         && receipt["OwnedProcessesStopped"].GetValue<bool>())
        "Prompt capture did not drain or stop its owned processes"

    if closed then
        check (receipt["ExitCode"].GetValue<int>() = 0) "Fake CLI child exit code was not preserved"
        check failure.IsSome "Early-closed stdin was silently accepted as successful completion"
        check (receipt["Reason"].GetValue<string>() = "InputFailed") "Input failure missing from durable receipt"
        check returned.IsNone "Failed input returned a completion token"

        check
            (not (receipt["InputCompleted"].GetValue<bool>())
             && receipt["Failures"].AsArray().Count = 1
             && receipt["Failures"].AsArray().[0].GetValue<string>().StartsWith("InputFailed:"))
            "Input failure receipt does not match the observed closed pipe"

        check
            (receipt["Stdout"].GetValue<string>() = token + Environment.NewLine)
            "Early-close receipt lost the misleading child completion token"

        check
            (TimeSpan.Parse(receipt["Elapsed"].GetValue<string>()) < TimeSpan.FromSeconds 45.0)
            "Early-closed stdin exceeded the phase budget and cleanup grace"
    else
        match failure with
        | Some ex -> raise ex
        | None -> ()

        check (receipt["ExitCode"].GetValue<int>() = 0) "Fake CLI child exit code was not preserved"

        check
            (receipt["InputCompleted"].GetValue<bool>()
             && receipt["Failures"].AsArray().Count = 0)
            "Successful prompt receipt did not record complete input"

        check
            (File.ReadAllBytes(Path.Combine(directory, "received.bin")) = expected)
            "Full prompt bytes, UTF-8 encoding or transport line terminator changed"

        let output, session = returned.Value
        check (output = token + Environment.NewLine) "Fake CLI completion token changed"

        let expectedSession =
            match resume with
            | Some name -> name
            | None -> sessionArgs[1]

        check (session = expectedSession) "Session identity changed"
        check (receipt["Reason"].GetValue<string>() = "Exited") "Prompt delivery did not complete cleanly"
        File.Copy(Path.Combine(directory, "received.bin"), Path.Combine(directory, title + ".received.bin"))

printfn "PASS actual runAgent prompt delivery (%s)" (if closed then "early-close" else "implementer and verifier")
