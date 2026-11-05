# Reporting security issues

Please do not include real credentials or private repository contents in a public report. If GitHub private vulnerability reporting is enabled, use **Security → Report a vulnerability**; otherwise open a minimal issue asking for a private contact without including exploit details.

Idea Lite operates on local files with the current user's permissions. Opening a project does not start Maven or execute its wrapper. Git commands use the installed Git executable and its existing credential setup. Commands such as commits may run the repository's configured Git hooks, as they do in Git itself. Explicit Maven tasks execute project plugins and wrappers.

Text operations have file/output/index limits, reject binary files and traversal paths, and do not follow filesystem links. Saving checks for external modifications before writing and again before atomic replacement; it is not a cross-process transaction or a backup system. Review changes before committing. This early release does not implement an isolated sandbox for untrusted repositories.
