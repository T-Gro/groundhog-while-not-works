# 🔨 groundhog-while-not-works : The Iteratorinator

```
while (!works) {
    try_again();
}
```

A coding agent trapped in its own Groundhog Day.
It wakes up. It writes code. The code is wrong. It wakes up. It writes code. The code is slightly less wrong. Repeat.

What it lacks in model quality and trust, it makes up for in **sheer, caffeinated, brute-force repetition**.

## FAQ

**Q: Is it smart?**
A: No. But it has lived this day four thousand times. It knows things.

**Q: When does it stop?**
A: When CI passes. Or February 3rd. Whichever comes first.

## Setup

Add a system alias to your shell config (`~/.zshrc` or `~/.bashrc`):

```bash
alias ralph='dotnet fsi /path/to/groundhog-while-not-works/Ralph.fsx --'
```

## Usage

**Basic invocation** - interactive mode:
```bash
ralph
```

**With a request:**
```bash
ralph "Add error handling to the parser"
```

**Auto-approve all prompts:**
```bash
ralph "Fix the flaky tests" --yes
```

**With CI monitoring** (requires a skill that tells it how to fetch CI errors):
```bash
ralph "Implement the new API endpoint" --yes --push
```

## License

Any derivatives of this work must keep using F#.

