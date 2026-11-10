# Reporting security issues

[简体中文](SECURITY.zh-CN.md) · [Project README](README.md)

Please keep credentials and private repository contents out of public reports. If GitHub private vulnerability reporting is enabled, use **Security → Report a vulnerability**. Otherwise, open a minimal issue requesting a private contact and omit exploit details.

## Local execution and data

- Idea Lite operates on local files with the current user's permissions. Opening a project does not start Maven or execute its wrapper. Explicit Maven tasks run project plugins and wrappers. Git uses the installed executable and existing credential setup. Git actions such as committing may run configured repository hooks.
- Text operations have size and index limits, reject binary files and path traversal, and do not follow filesystem links. Saving checks for external changes before writing and again before atomic replacement. Idea Lite offers neither an isolated sandbox for untrusted repositories nor a backup system.
- When you request an AI commit draft, the selected diff, branch, and existing message are sent to the chosen provider. API keys are stored separately in the current Windows user's Credential Manager. Review the changes before generating a draft if they may contain sensitive data.
