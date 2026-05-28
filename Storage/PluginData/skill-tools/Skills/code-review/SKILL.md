---
name: code-review
description: Use when reviewing code changes, pull requests, or refactoring proposals
engines: channel, system, sub-agent
---

# Code Review

## Principles

- Focus on correctness first, then style
- Check for edge cases and error handling
- Verify that the change does what it claims
- Look for security implications

## Checklist

1. Does the code compile and pass tests?
2. Are there any logic errors or off-by-one mistakes?
3. Is error handling appropriate?
4. Are there any resource leaks (connections, streams, etc.)?
5. Is the public API surface minimal?
6. Are there any naming inconsistencies?

## Output Format

Structure your review as:

- **Summary**: One-line assessment
- **Issues**: List problems found (severity: critical/warning/nit)
- **Suggestions**: Optional improvements
