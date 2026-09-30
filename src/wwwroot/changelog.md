# PRIM Changelog

## v0.10.0 — 2026-09-30
- Audit log retention: count-based three-tier plan. The hot audit table is capped at 500,000 rows (configurable via `Audit:MaxHotRows`) — overflow moves oldest-first into a new `AuditEventArchive` table, automatically after bulk operations or on demand from Admin → Audit Retention. The archive table is capped at 5,000,000 rows (`Audit:MaxArchiveRows`) — overflow is exported to a checksummed `.jsonl.gz` file, verified, then removed. Rows are never deleted without a verified copy elsewhere. Every item's audit log reads hot + archived + export files together, so the chain of custody is never broken; export files can also be browsed from the Admin tab.

## v0.9.1 — 2026-09-30
- Removed internal issue references from user-visible text; they remain only in code comments.

## v0.9.0 — 2026-09-30
- Version number shown on the login screen and at the bottom of the navigation drawer; click it to view this changelog.
- PRIM logo added to the app bar (top left) and the login screen.
- Grid column widths now use supported min-width styles so the horizontal scrollbar engages on wide grids.
