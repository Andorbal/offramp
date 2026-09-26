# Monolith \| migration

As of 2026-09-25 20:11 UTC.

- **Portable:** 20.3% of 20,200 lines are in standard, modern, or dual projects.
- **Framework-only:** 16,100 lines in 6 projects, down 4,100 since 2026-06-02.
- **Applications done:** 1 of 3.
- **Ready to port today:** 2 projects.

## Burn-down

| Scan | framework | dual | standard | modern | total |
|---|---:|---:|---:|---:|---:|
| 2026-06-02 09:00 | 20,200 | 0 | 0 | 0 | 20,200 |
| 2026-07-15 09:00 | 19,400 | 0 | 800 | 0 | 20,200 |
| 2026-08-30 09:00 | 16,800 | 3,300 | 800 | 0 | 20,900 |
| 2026-09-25 20:11 | 16,100 | 3,300 | 800 | 0 | 20,200 |

## By area

Lines of code by framework class.

| Area | projects | framework | dual | standard | modern | total |
|---|---:|---:|---:|---:|---:|---:|
| `legacy` | 2 | 6,500 | 0 | 0 | 0 | 6,500 |
| `src` | 5 | 8,700 | 3,300 | 800 | 0 | 12,800 |
| `tests` | 1 | 900 | 0 | 0 | 0 | 900 |

## Applications

| Application | Kind | Status | Projects left | Lines left | Port next |
|---|---|---|---:|---:|---|
| `src/App/App.csproj` | console | blocked | 2 of 3 | 6,600 | `src/Core/Core.csproj` |
| `src/Svc/Svc.csproj` | service | ready | 1 of 2 | 2,100 | `src/Svc/Svc.csproj` |
| `src/Web/Web.csproj` | web | done | 0 of 2 | 0 |  |

## Ready to port today

| Project | Lines | Dependents |
|---|---:|---:|
| `src/Core/Core.csproj` | 5,400 | 2 |
| `src/Svc/Svc.csproj` | 2,100 | 0 |
