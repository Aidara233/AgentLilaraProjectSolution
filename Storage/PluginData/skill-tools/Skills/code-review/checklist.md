# Detailed Code Review Checklist

## Security

- [ ] No hardcoded credentials or secrets
- [ ] Input validation on external data
- [ ] SQL injection / XSS prevention
- [ ] Proper authentication checks

## Performance

- [ ] No N+1 queries
- [ ] Appropriate use of async/await
- [ ] No unnecessary allocations in hot paths
- [ ] Caching where appropriate

## Maintainability

- [ ] Functions do one thing
- [ ] Naming is clear and consistent
- [ ] No dead code or commented-out blocks
- [ ] Error messages are actionable
