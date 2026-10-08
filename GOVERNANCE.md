# Governance

The project belongs to its contributors. It is independent of any company and of the Code Assurance Initiative, the
body that publishes the standard: that body qualifies engines, so it does not own one.

## Roles

- **Contributors** — anyone who opens an issue, reviews, comments, or sends a pull request.
- **Maintainers** — listed in [MAINTAINERS.md](MAINTAINERS.md). They merge pull requests, triage issues, cut releases
  and enforce the [Code of Conduct](CODE_OF_CONDUCT.md). Maintainers act for the project, not for their employer.

## Decisions

Decisions are made by **lazy consensus** in public, in issues and pull requests: a proposal stands unless someone
raises a substantive objection.

- Routine changes (fixes, tests, docs, a detector that follows the existing patterns) need one maintainer's approval
  that is not the author's own; with a single maintainer, that maintainer may merge after the checks are green.
- **Significant changes** — a new language, a change to the scoring fold or output formats, a new dependency, a change
  to this document or to the clean-room rule — are announced in an issue labelled for it and stay open for at least
  **7 days** before merging, so people in other time zones and organisations can respond.
- If an objection cannot be resolved in discussion, the maintainers decide by simple majority. A maintainer with a
  conflict of interest in the question does not vote.

## Becoming a maintainer

A contributor becomes a maintainer after **sustained contributions** — code, review, triage or documentation over a
period of months, not a single large patch. Any maintainer may nominate them in a public issue; if no maintainer objects
within **7 days**, they are added to MAINTAINERS.md. A maintainer may step down at any time; one who has been inactive for
6 months may be moved to emeritus by the same process.

## Diversity of maintainers

The aim is maintainers from **at least three organisations**. Once there are three or more maintainers, **no single
organisation (employer or its affiliates) may hold a majority of the maintainer seats**. If a change of employment or a
new nomination would break that, the affected organisation's maintainers agree among themselves who moves to emeritus
until the balance is restored. Individuals contributing in a personal capacity count as their own organisation.

## Relation to the standard

The project implements the published Code Assurance Index text. It takes part in the CAI engine qualification **like
any other engine** and gets **no special treatment**: no early access to unpublished text, no exemption from the
qualification rules, and no say over the result. Specification gaps found here are reported to the standard's public
repository, where anyone can see them.
