# Inference request contract implementation plan

> **For agentic workers:** Use superpowers:executing-plans and test-driven-development for this bounded review repair.

**Goal:** Close RV-130 and RV-141: every accepted JSON-schema spelling receives the same host enforcement, and invalid caller inference controls fail explicitly before inference.

**Architecture:** Extend the existing Core request validation, shared by native preflight and OpenAI live/batch mapping. Preserve the existing optional controls and inclusive supported bounds. Use the same trimmed, ordinal-insensitive response-format interpretation at validation, wire construction, and both host structured-output decisions. Keep the request and its schema immutable. OpenAI errors name the original wire parameter; native errors name the native field. No provider traffic, persistence, schema or configuration changes.

**Tech stack:** .NET 10, source-generated System.Text.Json, xUnit, existing fake chat clients and authenticated API fixtures.

**Spec:** The dated review register RV-130/RV-141 and owning API/CLI parameter contracts.

## Decisions and constraints

The review's proposal to document silent clamping would preserve surprising behavior, so this repair instead rejects invalid explicit controls. Case variants already reach the provider and remain supported, with surrounding whitespace normalized consistently. Bounds are finite temperature 0..2, topP 0..1, presence/frequency penalty -2..2 and positive maxOutputTokens. An explicit response format must be text, json_object or json_schema; json_schema needs an object schema, directly or in the existing wrapper. A present malformed OpenAI response_format must not disappear during mapping. A shadowed invalid max_tokens must still be rejected when max_completion_tokens is present. No new domain concept is introduced.

## Tests and implementation

- [x] Add behavioral RED cases for mixed-case/whitespace strict schema in buffered and streamed real Wizard execution, proving correction and no invalid text leakage. Preserve lowercase and ordinary JSON controls.
- [x] Add Core boundary cases for invalid, nonfinite and boundary values, omitted options, unknown/blank formats and missing/malformed schemas. Add authenticated native buffered/streamed and OpenAI buffered/streamed/batch validation cases proving a field-specific 400 and zero fake inference calls. Include both token aliases and malformed response-format shapes.
- [x] Implement shared bounded validation, consistent host/wire format handling and OpenAI parameter mapping. Retain validation errors in safe preflight diagnostics without logging caller content. Remove silent clamps only after validation covers every caller.
- [ ] Run owning and affected classes, causal mutations with exact restoration, zero-warning build and owned formatting. Update API and DESIGN with the same change. Independent review and exact combined qualification precede finding credit.

Review focus: direct in-process nonfinite values; both streaming and buffered schema enforcement; unsupported Boolean schema roots (the existing documented parser subset accepts object roots only); OpenAI mapping that discards invalid input; existing prompt/spell entrypoints and safe failure classification. No installation or live model/embedding tests.

Validation uncovered three related contract edges and kept their failures: malformed schema types/wrapper metadata and missing safe preflight classification (11 failing cases with 38 controls), then seven missing delegation field diagnostics and seven missing central HTTP400 mappings. The final focused repaired run passed 88 cases before two direct in-process Wizard controls were added. Initial namespace-only compiler failures and incorrectly scripted streaming fixtures are retained but excluded from behavioral RED. The canonical original RED is 48 failures/15 controls on unchanged production.

A direct in-process schema deeper than the JSON parser limit initially threw (one genuine RED, one shallow control). It now returns the same safe InvalidResponseSchema validation code; the exact catch-removal mutant reproduces that failure. The API error catalog also now names all seven new codes. An initial 30-class run retained 1,388 passes and one host-startup failure in an existing memory-delegation fixture. The isolated complete class passed; the multi-host cause remains under investigation and is not treated as a green gate.
