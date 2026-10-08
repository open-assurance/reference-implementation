# Vendored schemas

`cai-delivery-1.0.schema.json` is the published CAI delivery schema (Apache-2.0, from the Code Assurance Index
specification repository, `schemas/` directory). The evidence bundle this engine writes is validated against its
`$defs/evidence` definition in the test suite (`EvidenceSchemaTests`). Refresh it from the specification when a new
schema version is published; the file is copied verbatim and never edited here.
