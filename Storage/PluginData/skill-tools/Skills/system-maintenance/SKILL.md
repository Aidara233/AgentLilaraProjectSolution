---
name: system-maintenance
description: Use when performing system health checks, diagnostics, or maintenance tasks
engines: system, sub-agent
---

# System Maintenance

## Health Check Procedure

1. Check engine status (active loops, error counts)
2. Verify database connectivity
3. Check disk space and log sizes
4. Review recent error logs for patterns

## Common Diagnostics

- **Memory usage**: Check if extraction/search is healthy
- **Loop health**: Check for stuck or crashed loops
- **Adapter status**: Verify platform connections
- **Scheduled tasks**: Verify cron jobs are running

## Escalation

If a critical issue is found:
1. Document the symptoms clearly
2. Include relevant log excerpts
3. Suggest a fix if obvious, otherwise escalate to admin

## Output Format

```
[OK/WARN/FAIL] Component: brief status
```
