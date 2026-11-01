export function buildAiPrompt({ feature, version, os, arrangement, shell, names }) {
  return `Use the attached CherBot AI reference (version ${version}) as the source of truth.
I want: ${feature || '[describe the feature]'}
Engine OS: ${os}
Arrangement: ${arrangement}
Preferred shell: ${shell}
Exact integration names I supplied: ${names || '[ask me for any names or IDs you need]'}

Return a complete set of CherBot CLI commands to configure this feature on a running engine, plus every required JSON file with its filename and full contents.
Use cherbot api METHOD /v1/path --body-file file.json and cherbot pack preview/apply as documented. Use --url for my engine address and --token-file for a token file kept on my computer; use placeholders for both. Never ask me to paste credentials into this chat.
Include prerequisites, a read-only inspection or pack preview first, application commands, verification, and exact removal instructions. Use shell quoting appropriate for ${shell}. Stop on errors. Explain any commands that send chat, play media or change OBS.
Use only supported CLI flags, API routes, step IDs, trigger IDs and variables from the reference. Do not invent APIs or encoded exports. If information is missing, ask for it. If a capability is unsupported, say so.
Preserve existing workflows unless I explicitly request replacement. Avoid offline database imports while the engine is running. Do not include account secrets, viewer data or private configuration in output.
For follow-up troubleshooting, request only the smallest relevant redacted error or run trace.`;
}
