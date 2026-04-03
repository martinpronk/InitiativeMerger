# Contributing to Azure Initiative Merger

Thank you for your interest in contributing! This project uses a **fork-based workflow** — you do not need write access to the repository.

## How to contribute

### 1. Fork the repository

Click the **Fork** button in the top-right corner of the [repository page](https://github.com/martinpronk/InitiativeMerger). This creates your own copy of the project under your GitHub account.

### 2. Clone your fork

```bash
git clone https://github.com/your-username/InitiativeMerger.git
cd InitiativeMerger
```

### 3. Set up the commit message template

Run this once after cloning — it pre-fills your editor with a guided template whenever you run `git commit`:

```bash
git config commit.template .gitmessage
```

### 4. Add the upstream remote

This lets you pull in future changes from the original repository:

```bash
git remote add upstream https://github.com/martinpronk/InitiativeMerger.git
```

### 5. Create a feature branch

Never work directly on `main`. Create a branch with a descriptive name:

```bash
git checkout -b feature/your-feature-name
# or for bug fixes:
git checkout -b fix/short-description
```

### 6. Make your changes

- Keep changes focused — one feature or fix per pull request
- Make sure the project still builds: `dotnet build`
- Test your changes locally by running the web app: `dotnet run --project src/InitiativeMerger.Web`

### 7. Commit and push to your fork

```bash
git add .
git commit   # opens the template in your editor
git push origin feature/your-feature-name
```

### 8. Open a pull request

Go to your fork on GitHub. You will see a **Compare & pull request** button. Click it, fill in a clear title and description, and submit the PR against the `main` branch of the original repository.

## Keeping your fork up to date

Before starting new work, sync your fork with the upstream repository:

```bash
git fetch upstream
git checkout main
git merge upstream/main
git push origin main
```

## Guidelines

- **Open an issue first** for significant changes so the approach can be discussed before you invest time in it
- Write clear commit messages that explain *why* a change was made, not just *what*
- Do not commit build output (`bin/`, `obj/`) or user-specific files (`.vs/`, `*.user`)
- Policy resource IDs added to `WellKnownInitiative.cs` must be verified against the official Azure built-in initiative list

## Questions

Open a [GitHub issue](https://github.com/martinpronk/InitiativeMerger/issues) if you have questions or run into problems.
