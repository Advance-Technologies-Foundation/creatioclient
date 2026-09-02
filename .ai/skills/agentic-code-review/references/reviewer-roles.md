# CreatioClient reviewer roles

The canonical reviewer behavior lives in these files:

- code quality and maintainability: `.ai/agents/code-quality-reviewer.md`
- security: `.ai/agents/security-reviewer.md`
- performance and resource lifetime: `.ai/agents/performance-reviewer.md`
- testing: `.ai/agents/testing-reviewer.md`
- bugs and edge cases: `.ai/agents/bug-reviewer.md`
- intent: `.ai/agents/intent-agent.md`
- KISS: `.ai/agents/kiss-agent.md`

Read the applicable role file from the repository root. Every reviewer returns only actionable, evidenced
findings using the shared severity, file/line, trigger, impact, evidence, and smallest-fix contract, or `No
findings`.
