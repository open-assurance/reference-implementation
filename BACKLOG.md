# Backlog — defects found by measuring on real code

Source: 5 real, unmodified open-source C# repositories, every defect and trap keyed by hand (measured 2026-10-08).
Reference recall there: 19.0 %, trap resistance 78.5 %.

## False positives (precision)

| # | defect | evidence (unit — site) |
|---|---|---|
| R-01 | TODO markers located 8 lines below the comment (83 for a comment on 75) | DomainResult — src/Common/DomainResultOfT.cs:75, src/Common/IDomainResultOfT.cs:34 |
| R-02 | `FROM <stage> AS x` treated as a mutable image reference (stage alias, not an image) | every Dockerfile unit, lines 14/16/17/19 |
| R-03 | `test-without-assertion` blind to assertions inside helper methods (`Then_Response_Is_*`, `AssertBrokenRule`) — 22 FPs, most of the 84 % noise on DomainResult | DomainResult tests/Mvc/*; cqrs-api MoneyValueTests:21,59 |
| R-04 | `null-dereference` fires after `x?.Y.Any() == true ? … x.Y` (the deref only runs when the guard was true) | DomainResult src/Mvc/ActionResult/DomainResultToActionResult.cs:60, IResult/DomainResultToResult.cs:60; NetCoreMediatrSample StronglyTypedIdSchemaFilter.cs:14 |
| R-05 | Abstract base controllers (no actions) reported as unauthenticated | EquinoxProject ApiController.cs:8, BaseController.cs:6 |
| R-06 | `secret-in-version-history` on placeholders and on a page-name constant (`CHANGETHISSECRETKEY`, `ChangePassword => "ChangePassword"`) | EquinoxProject history |
| R-07 | `unused-code` on EF Core-mapped backing fields / ORM-read fields | cqrs-api Customer.cs:19, Order.cs:19, Product.cs:15 |

## Recall gaps (where the reference engine is lowest)

| # | concept | recall | note |
|---|---|---|---|
| R-10 | deprecated-dependency (75) | 0 % | offline registry data; transitive graph not read |
| R-11 | vulnerable-dependency (44) | 15.9 % | offline advisory data; vendored JS + CDN tags not read |
| R-12 | mutable-image-reference (14) | 7.1 % | floating version tags (`aspnet:7.0`) not treated as mutable |
| R-13 | unused-code (92) | 2.2 % | only write-only fields; no unreferenced-member analysis |
| R-14 | incomplete-implementation (29), not-implemented-placeholder (10), unused-dependency (17), dual-write (13), improper-resource-disposal (11), silent-error-fallback (6), null-dereference (5), domain-event-never-handled (4) | 0 % | |
| R-15 | missing-authorization (17) | 0 % | `[AllowAnonymous]` PII actions, ControllerBase controllers, gRPC services |
| R-16 | missing-cancellation-propagation (118) | 29.7 % | accepted-but-not-forwarded tokens, Quartz `context.CancellationToken`, Razor page handlers |
| R-17 | suppressed-diagnostic (21) | 4.8 % | `[SuppressMessage]`, `<NoWarn>` in .csproj / Directory.Build.props |
| R-18 | end-of-life-platform (45) | 75.6 % | misses Dockerfile EOL images and retired products (azure-sql-edge) |

Clean-room note: these come from scoring against keys built by hand; nothing here was read from another engine's source or mappings.
