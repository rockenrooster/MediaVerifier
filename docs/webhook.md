# Webhook

MediaVerifier can post a short summary to an address of your choice after each scheduled scan
(Settings → Notifications → Webhook). It is meant for home dashboards and notification services
such as ntfy, Gotify or Home Assistant. The summary carries counts only: no file names, folder names
or paths.

## Addresses

| Address | Accepted |
|---------|----------|
| `https://…` | Yes |
| `http://` to `localhost`, 127.0.0.0/8, ::1, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, fc00::/7 or fe80::/10 (written as an IP address) | After you confirm sending unencrypted |
| Anything else, including `http://` to a public address or a host name other than `localhost` | No |

Leaving the box empty turns the webhook off. *Send test* posts the test payload below and says
whether the address answered with a 2xx status.

## Delivery

- One `POST` with `Content-Type: application/json; charset=utf-8` and
  `User-Agent: MediaVerifier/<version>`.
- 10-second timeout per attempt; two retries, after 2 and 4 seconds. Redirects are not followed.
  A 3xx or 4xx answer (except 408 and 429) ends the attempts.
- Sent after the run is saved and its notification shown. A failure is written to the log and never
  changes the run's result.

## Payload

Property names are camelCase. Example after a scheduled run:

```json
{
  "event": "run.finished",
  "app": "MediaVerifier",
  "version": "1.0.0.12",
  "runId": 123,
  "planId": 2,
  "trigger": "scheduled",
  "status": "completed",
  "finishedUtc": "2026-11-17T03:12:40Z",
  "durationSeconds": 5532.4,
  "filesSeen": 12507,
  "filesAnalysed": 412,
  "filesVerified": 1860,
  "errors": 0,
  "newFindings": { "critical": 1, "high": 2, "medium": 0, "low": 0 },
  "newFindingsByKind": { "audio": 2, "video": 1 },
  "alerts": [ "RUN-ROOT-OFFLINE" ]
}
```

| Property | Meaning |
|----------|---------|
| `event` | `run.finished`, or `test` for *Send test* |
| `runId`, `planId` | The run's number (0 for a test) and the schedule that started it |
| `trigger` | `scheduled`, `manual` or `resume` |
| `status` | The run's result in lower case: `completed`, `stopped`, `failed`, `interrupted` and so on |
| `finishedUtc`, `durationSeconds` | When the run ended (ISO 8601, UTC) and how long it took |
| `filesSeen`, `filesAnalysed`, `filesVerified`, `errors` | The run's counters |
| `newFindings` | Problems first found in this run, by tier |
| `newFindingsByKind` | The same problems by media kind (`photo`, `video`, `audio`, `other`); kinds without any are left out |
| `alerts` | Run-level alert ids, such as `RUN-READ-ERRORS`, `RUN-MASS-MISSING` and `RUN-ROOT-OFFLINE` |
