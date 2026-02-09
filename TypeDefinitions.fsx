open System

type SafeMarkup = SafeMarkup of string
type RawText = RawText of string

module SafeMarkup =
    let value (SafeMarkup s) = s
    let literal s = SafeMarkup s
    let concat (parts: SafeMarkup list) = parts |> List.map value |> String.concat "" |> SafeMarkup

module RawText =
    let value (RawText s) = s
    let escapeForMarkup (RawText s) = SafeMarkup (s.Replace("[", "[[").Replace("]", "]]"))

// DoD = Definition of Done - each item is a technically executable criterion
type DoDResult = { Criterion: string; Passed: bool option }  // None = not yet evaluated

// Sprint item - represents a single sprint file
type BacklogItem = { 
    FilePath: string       // Absolute path to sprint file
    Order: int             // Sort order from filename (01_, 02_, etc.)
    Name: string           // Sprint name from filename
    Description: string    // Full description from file body
    DoD: string list       // Definition of Done from file body
}

// Phase is just Implement - verifiers handle all validation
type Phase = Implement

// Track status AND iteration count for each verifier
type VerifierStatus = NotStarted | Passed of iterations: int | Failed of iterations: int

// Track full history of an iteration for retry context
type IterationRecord = {
    Iteration: int
    AgentOutput: string
    VerifierResults: (string * bool * string) list  // (verifierName, passed, summary)
}

type BacklogItemTiming = {
    StartTime: DateTime
    EndTime: DateTime option
    IterationReasons: (int * string) list  // (iteration, reason for retry)
    Summary: string option
    LastDoDResults: DoDResult list         // DoD results from last iteration
    VerifierResults: Map<string, VerifierStatus>  // Track each verifier
    IterationHistory: IterationRecord list  // Full history for retry context
    ApprovedCommits: Map<string, string>  // Commit hash when verifier passed (for incremental checks)
}

type BacklogStatus = 
    | Todo
    | Running of phase: Phase * iteration: int
    | Done of iterations: int

type State = {
    Backlog: (BacklogItem * BacklogStatus * BacklogItemTiming) list
    StartTime: DateTime
    Message: string
    AgentStartTime: DateTime option  // When agent started (None = idle)
    TotalEstimatedIterations: int  // For progress calculation
    CompletedIterations: int
    FinalVerifierResults: Map<string, VerifierStatus>  // Final verification after all sprints
    FinalVerifierSummaries: Map<string, string>  // Management summaries from final verifiers
    CIStatus: (string * bool option) option  // (PR URL, passed: None=pending, Some true=passed, Some false=failed)
    CurrentPhase: string  // "Planning", "Executing", "Final Verification", "Complete"
    CurrentAgentTask: string  // What agent is currently doing
    LastVerifierLog: (string * bool * string) option  // (verifier name, passed, summary)
    PlanOverview: string  // Brief overview of the plan
    ErrorLog: string option  // Last error if any
}

let emptyTiming = { 
    StartTime = DateTime.MinValue
    EndTime = None
    IterationReasons = []
    Summary = None
    LastDoDResults = []
    VerifierResults = Map.empty
    IterationHistory = []
    ApprovedCommits = Map.empty
}

let emptyState = {
    Backlog = []
    StartTime = DateTime.Now
    Message = ""
    AgentStartTime = None
    TotalEstimatedIterations = 0
    CompletedIterations = 0
    FinalVerifierResults = Map.empty
    FinalVerifierSummaries = Map.empty
    CIStatus = None
    CurrentPhase = "Initializing"
    CurrentAgentTask = ""
    LastVerifierLog = None
    PlanOverview = ""
    ErrorLog = None
}
