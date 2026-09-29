# Skypeek for AWS

A read-only Windows tray tool for everyday AWS lookups, built so you don't need the CLI or the console for them:

- **Secrets Manager and SSM Parameter Store:** keeps a searchable list of names and metadata, refreshed on a schedule per account and region. You can reveal and copy a value on demand.
- **Elastic Beanstalk:** shows environment health, failed deploys, EC2 CPU (and memory when the CloudWatch agent publishes it), and CloudWatch alarms.
- **ECS:** shows service health, rollouts, stopped-task reasons, CPU and memory from CloudWatch, and CloudWatch alarms; can force a new deployment (elevated key, approved per call).
- **RDS:** lists databases and clusters with read replicas as a tree, health, CPU, connections, storage and events; copies host names; shows live and historical database logs. Read-only.
- **ElastiCache:** lists Redis/Valkey replication groups, shards and nodes, standalone clusters and serverless caches, with endpoints, health, CPU, memory, connections, hit rate and evictions. Read-only.
- **EC2:** lists instances that Elastic Beanstalk does not already show, with state, status checks, scheduled events, IP addresses, security groups, CPU and memory; can start, stop and reboot one instance (elevated key, approved per call).
- **Load balancers:** ALB/NLB with target groups and per-target health, traffic, alarms, and listener routing rules; read-only, with links to manage them in the console.
- **Network tab:** VPCs, subnets with address usage, every IP address (network interface) and what uses it, Elastic IPs, and security groups with their rules; downloaded on a schedule and cached. Security group rules can be added, changed and deleted (elevated key, approved per call).
- **Hide from the dashboard:** any resource you don't care about can be hidden; it is then not shown, not counted and not queried for metrics.
- **Request log:** records every AWS call, with no values or credentials.
- **Encrypted local store:** unlocked with a master password.

## Build and run

```bash
dotnet build Skypeek.sln
dotnet test tests/Skypeek.Tests
dotnet publish src/Skypeek.App -c Release -o publish
```

`publish/Skypeek.exe` is a single file that needs the .NET 10 Desktop Runtime.

## First run

1. Start `Skypeek.exe` and create the master password.
2. **Settings → Accounts & regions:** pick a profile from `%USERPROFILE%\.aws\credentials` and one or more regions, then click **Add selected** and **Save**. Each profile + region pair is a *target*, with its own features, schedules and threshold overrides.
3. Press **Win+Alt+A** (or click the tray icon) to open the main window. It has four tabs:
   - **Secrets & parameters:** search on the left, details and reveal on the right.
   - **Dashboard:** a tree of target → Elastic Beanstalk / ECS / RDS / ElastiCache / EC2 / load balancers / CloudWatch alarms, with a details panel for the selected item.
   - **Logs**
   - **Network:** target → VPC → subnets → IP addresses, and VPC → security groups.
   - **Request log**
   - **Settings**

   Closing the window hides it to the tray.

## Where credentials come from

- **Credentials file** (`~/.aws/credentials`, or `AWS_SHARED_CREDENTIALS_FILE`): static or temporary keys, as before.
- **AWS SSO / IAM Identity Center** profiles created with `aws configure sso` (in `~/.aws/config`, or `AWS_CONFIG_FILE`; both the `[sso-session]` and the older per-profile layout). Skypeek reads the token that `aws sso login` caches in `~/.aws/sso/cache` and exchanges it for role credentials with `sso:GetRoleCredentials` (a logged, allowlisted read; the token is never logged or stored). It never refreshes or writes the token itself.
  - When the sign-in has expired, the profile shows **AWS SSO sign-in required**: nothing is sent to AWS, the tray turns red, and the dashboard offers **Sign in** (runs `aws sso login --profile …` in a console; approve in your browser). The profile resumes by itself when the new token appears.
- **A name in both:** SSO is used while you are signed in, the credentials-file keys otherwise. Settings shows which source each profile uses.

## Two keys per target

Each target has a **read-only profile**, used for everything including background refresh, and an optional **elevated profile**.

- The elevated profile is used only when you ask, for example with **Reveal with elevated key** for a secret the read-only role can't read.
- Every elevated call shows a permission dialog naming the operation, resource and profile. **Deny** is the default button.
- Elevated calls go through the same read-only allowlist, so they can't write.
- The request log marks elevated calls; filter them with **Elevated key only**.
- **Exception: one key for both.** For an account with no separate read-only role, choose **Same key as read-only** as the elevated profile. You confirm a warning first, the dashboard and every permission prompt flag it, and elevated actions still ask each time; the read allowlist still applies to every call.
- **Add targets** lists only `*ReadOnly*` roles by default; tick **All profiles** to pick another role (e.g. `AWSAdministratorAccess`) as the read-only key, after a warning.

## Logs and root cause

- **ECS container logs:** in the Dashboard, select a service and click **View logs**. Skypeek reads the service's task definition (`ecs:DescribeTaskDefinition`) to find each container's `awslogs` group and stream prefix, then shows the lines from CloudWatch Logs (`logs:FilterLogEvents`).
  - You can pick a time range, use a filter pattern (e.g. `ERROR` or `"Timeout"`) and turn on live tail.
  - Up to 3,000 lines are shown; narrow the range or filter to see older ones.
- **Elastic Beanstalk logs:**
  - **CloudWatch log groups:** browsed the same way. These exist only if log streaming is on for the environment (`/aws/elasticbeanstalk/<env>/...`).
  - **Request last 100 lines / Request full bundle:** asks EB to collect the logs from the instances, as the console's "Request logs" does. The tail is shown in the app; bundles open as zip downloads in your browser.
- **Root cause:** for any environment that isn't Green, each health poll also reads EB enhanced health. The details panel shows a "Why" section with environment causes (e.g. "30 % of the requests are erroring with HTTP 5xx"), the request error summary, and the causes for each failing instance (e.g. "Process default has died"). The first causes also appear in the tree.
- **RDS logs:** select a database and click **View logs**. The source list has:
  - log groups the database exports to CloudWatch Logs (`/aws/rds/instance/<id>/…`, or the cluster's groups filtered to this instance), with time range, filter pattern and live tail
  - the database's own log files (`rds:DescribeDBLogFiles`), newest first. Selecting one shows its last 3,000 lines (`rds:DownloadDBLogFilePortion`); the filter box keeps only lines containing the text. **Live tail** follows the file and moves to the next file when the engine rotates it (e.g. hourly for PostgreSQL).
- **Log lines are never stored.** They are cleared when the app locks.

## Search, ECS tasks and usage history

- **Search** (Ctrl+F in the dashboard) filters the tree to resources whose name, subtitle or details contain every word you type: names, instance/task IDs, IPs, endpoints, image names, versions, account IDs. The path to each match opens; Enter jumps to the first match, Esc clears.
- **ECS tasks and containers:** each service lists its running tasks (status, health, task definition, IP, zone, uptime) and each task its containers (status, health, exit code, image). A container's **View logs** opens its CloudWatch stream for that task only. Tasks are read with the health poll (`ecs:ListTasks` + `ecs:DescribeTasks`, batched per cluster).
- **Usage (last 30 days)** in the EB, ECS, RDS, ElastiCache and EC2 details reads hourly CPU and memory history from CloudWatch on demand (Skypeek itself keeps only the last hour): average, minimum, maximum and p95 per node, how many hours peaked at ~100% (≥99%) or ≥90%, and a verdict:
  - **Under-provisioned** when any node peaks at ~100% in at least 2% of hours, or its typical load (p95 of hourly averages) is 80% or more.
  - **Over-provisioned** when every metric's p95 stays below 30% and its peak below 60% (CPU) or 50% (memory).
  - **Right-sized** otherwise; no verdict with less than 24 hours of data.
  - EC2 memory needs the CloudWatch agent; RDS memory is estimated from `FreeableMemory` and the instance size (marked ≈). Results are kept for an hour and cleared on lock.

## Elastic Beanstalk versions, nodes and actions

- **Versions:** each environment shows its deployed version with its creation date, and whether it is the newest version of the application. The tree marks environments that are behind with "not latest version".
- **Nodes:** each EC2 instance of the environment is listed with its EC2 state, type, zone, IP and launch time, its EB health and causes, the version deployed on it, and CPU/memory.
- **Actions:**

  | Action | AWS call | Effect |
  |---|---|---|
  | Deploy latest version / Redeploy current version | `elasticbeanstalk:UpdateEnvironment` | Changes only the version. The pipeline rejects any other change, such as configuration, platform or tier. |
  | Restart app servers | `elasticbeanstalk:RestartAppServer` | Restarts the application on all instances, without an EC2 reboot. |
  | Reboot | `ec2:RebootInstances` | Reboots exactly one instance. |
  | Terminate | `ec2:TerminateInstances` | Terminates exactly one instance; the Auto Scaling group replaces it. You must type the instance ID to confirm. |

  Reboot and Terminate first re-check that the instance still belongs to the environment. The standalone EC2 actions are under [EC2 instances](#ec2-instances).

## RDS and ElastiCache

- **RDS tree:** Aurora and Multi-AZ DB clusters list their writer and readers. Read replicas sit under their source instance; replicas in other regions are listed by ARN (add that region as a target to monitor them).
- **Details:** endpoint with **Copy host** (cluster writer, reader and custom endpoints for clusters), status, replication state and lag, Multi-AZ, parameter group and pending changes, and recent RDS events.
- **Load:** CPU, connections, storage used (not Aurora, which grows automatically) and freeable memory. Connections are compared with `max_connections`: the parameter group's value when it sets one (a number or a formula), otherwise the engine's default formula evaluated for the instance size. Estimates are marked ≈; for engines without a known default (e.g. SQL Server) connections are shown but not evaluated.
- **ElastiCache:** replication groups show primary/reader or configuration endpoints (copy buttons), encryption and failover settings, and every node with its role, zone, endpoint, engine CPU, memory, connections, hit rate, evictions and replication lag. Sharded (cluster-mode) groups list their shards. Skypeek does not connect to Redis or read keys.
- **Problems:** a database or cache that is not available (e.g. `storage-full`, `failed`, `incompatible-*`), broken replication, a shard without a primary, recent failure events, thresholds (CPU, connections % of max, storage used; engine CPU and memory for caches) and alarms on `AWS/RDS` or `AWS/ElastiCache` metrics. Routine states such as `backing-up` or a deliberately `stopped` database do not count. Thresholds are in Settings → Suppressions & thresholds, and per resource from **Thresholds…**.
- **No database actions:** there is no reboot, failover, modify or delete; the architecture test checks that no such request type is referenced.
- **Permissions:** the read-only role needs `rds:Describe*`, `rds:DownloadDBLogFilePortion` and `elasticache:Describe*` (if the role lacks one, the dashboard or Logs tab shows the access error). EC2, load balancers and the Network tab need `ec2:Describe*` and `elasticloadbalancing:Describe*` (all in AWS ReadOnlyAccess). The elevated role needs `ec2:StartInstances`, `ec2:StopInstances`, `ec2:RebootInstances` and the security group rule calls above only if you use those actions.

## Elastic Beanstalk applications

- **EB by application** in the dashboard toolbar groups environments under their application; otherwise they are listed directly under Elastic Beanstalk with the application name in front.
- Select an application (or click **Application versions** on an environment) to see its environments with the version each runs, and all its versions, newest first, with creation date, status, description, source bundle and where each one is deployed.

## EC2 instances

- **Which instances:** every instance that is not terminated, except those Elastic Beanstalk manages (they are under their environment). The EC2 group's details have a switch to list those too.
- **Details:** state and reason, who manages it (Auto Scaling group, Elastic Beanstalk, ECS capacity provider, spot), status checks, scheduled maintenance, private/public IPs and DNS names, each network interface with its addresses, and its security groups as buttons that open them on the Network tab. CPU (`AWS/EC2 CPUUtilization`), memory with the CloudWatch agent, **Usage (last 30 days)** and alarms.
- **Problems:** a failed system or instance status check is critical; a scheduled reboot or retirement is a warning; CPU/memory above the EC2 thresholds (Settings → Suppressions & thresholds, or **Thresholds…**); alarms on the instance, including `StatusCheckFailed`. A stopped instance is greyed out and is not a problem.
- **Actions:** **Start…**, **Reboot…** and **Stop…** (`ec2:StartInstances`, `ec2:RebootInstances`, `ec2:StopInstances`), one instance at a time. Each re-checks the instance's state first. Stop is a normal shutdown (never force or hibernate), explains what is lost (instance store data, a non-Elastic public IP, replacement by an Auto Scaling group) and asks you to type the instance ID. There is no terminate for standalone instances.
- **Calls:** `ec2:DescribeInstances` and `ec2:DescribeInstanceStatus` with the health poll.

## Load balancers

- **Tree:** load balancer → target groups, each with a healthy/total bar. A target group's details list its targets with state, reason and zone; **Show instance** selects the EC2 instance (or its EB environment) on the dashboard.
- **Details:** DNS name (copy), scheme, state, zones, security groups (open on the Network tab), target groups with their health check, requests and 5xx counts of the last hour (ALB) or active flows (NLB), alarms, and **Listeners and routing rules**: every listener with its rules in priority order, conditions (host, path, header, method, query, source IP) and actions (forward with weights, redirect, fixed response, authenticate). Rules are read when the details open and kept for 10 minutes.
- **Problems:** a target group with unhealthy targets is a warning, one with no healthy target is critical; a failed or impaired load balancer; alarms on `AWS/ApplicationELB`/`AWS/NetworkELB` for the load balancer or its target groups. Target group problems can be suppressed like EB causes.
- **Read-only:** change listeners, rules and targets with **Manage in console**; no ELB write call is on the allowlist.
- **Calls:** `elasticloadbalancing:DescribeLoadBalancers`, `DescribeTargetGroups` and one `DescribeTargetHealth` per target group (8 at a time) with the health poll; `DescribeListeners` and `DescribeRules` when details open.

## Network (VPC) and security groups

- **Tree:** target → VPC → **Subnets** (CIDR, zone, public/private, used/free addresses; nearly full subnets are flagged) → the IP addresses in each subnet, with what uses them (EC2 instance, load balancer, RDS, ElastiCache, ECS task, Lambda, NAT gateway, VPC endpoint, …); VPC → **Security groups**; **Elastic IPs** (unassociated ones are flagged). Search finds IPs, CIDRs, ids, names, ports and rule sources.
- **Cached:** the inventory is downloaded by its own job (default hourly, per target in Settings → Accounts & regions; **Download all now** on the tab) and stored in the vault, so the tab opens instantly; the tab shows when it was downloaded.
- **Security group details:** inbound and outbound rules (protocol, ports, source, description, rule id; rules open to 0.0.0.0/0 or ::/0 are highlighted), the interfaces that use the group, and the groups that allow it as a source.
- **Editing rules:** **Add inbound/outbound rule…**, **Edit…** and **Delete…**. The editor has presets (SSH, RDP, HTTP, HTTPS, PostgreSQL, MySQL, MSSQL, Redis, all traffic, …), IPv4/IPv6 CIDR, security group or prefix list sources, **My IP** (asks `checkip.amazonaws.com`) and a description. Each change is one rule with one source: `ec2:AuthorizeSecurityGroupIngress/Egress`, `ec2:ModifySecurityGroupRules` or `ec2:RevokeSecurityGroupIngress/Egress` by rule id. Before an edit or delete Skypeek re-reads the rule and stops if it changed. Deleting a rule, or opening one to the whole internet, asks you to type the group ID. After the change the group is re-read.
- **Calls:** `ec2:DescribeVpcs`, `DescribeSubnets`, `DescribeNetworkInterfaces`, `DescribeSecurityGroups`, `DescribeSecurityGroupRules`, `DescribeAddresses`.

## Hiding resources

- Right-click a resource in the tree and choose **Hide from dashboard**, or click **Hide** in its details. Works for every resource type (environments, services, databases, caches, instances, load balancers).
- A hidden resource is not shown, not counted as a problem, never turns the tray red, never notifies, and its metrics are not queried (no CloudWatch cost). It stays in the inventory, so unhiding is instant.
- **Show hidden (N)** in the toolbar shows them greyed out; **Suppressions** lists them with **Unhide**.

## Non-read actions

These are the only calls that aren't pure reads: the EB log request (`RequestEnvironmentInfo`), the EB actions above, ECS **Force new deployment** (`ecs:UpdateService` with only `ForceNewDeployment`; any other field, such as desired count or task definition, is refused by the pipeline), EC2 **Start/Stop/Reboot** of one instance (stop without force or hibernation), and security group rule changes (exactly one rule with one source, by group id; deletes and edits by rule id). Force new deployment replaces a service's tasks with fresh ones from the current task definition, e.g. to pull a re-tagged image or re-read secrets.

- They always use the target's **elevated** profile, never the read-only one. Targets without an elevated profile can't use them.
- Each call asks for your approval in the permission dialog.
- They are on a separate confirmed-only list: the SDK pipeline lets them through only for a call that is both elevated and approved, and only with the narrow parameters above.
- Everything else, including every background call, is read-only.

## Exporting logs for AI analysis

**Export for AI…** (Markdown or JSON Lines) and **Copy for AI** in the Logs tab produce everything an AI assistant needs in one file or paste:

- an analysis instruction
- the resource, target, source, time range and filter
- the resource's current health state: EB causes, request error rates, failing nodes and recent events, or ECS deployments, stopped-task reasons and service events
- the shown log lines, oldest first

Exported files are **not encrypted**; delete them when you're done.
## Suppressing alarms

- From the dashboard, select an alarm and click **Suppress…**. Enter an exact name or a `*`/`?` pattern and choose this target or all targets.
- Manage rules in Settings → Alarms & thresholds.
- Suppressed alarms stay visible, greyed out, but never count as problems, never turn the tray icon red and never notify.
- **EB health causes** can be suppressed the same way. In an environment's "Why" section, click **Suppress…** next to a cause and choose this environment, all environments of the target, or all targets. Matching ignores spacing and line breaks and accepts `*` wildcards. If every cause behind a Red/Yellow status is suppressed, the environment counts as OK, unless something else is wrong: a failed deploy, error events, thresholds or alarms.
- **Suppressions** in the dashboard toolbar lists every alarm and cause rule with Remove; they are also in Settings → Suppressions & thresholds.
- Auto-scaling target-tracking alarms (`TargetTracking-*`) are ignored by default, because they sit in ALARM whenever a service is scaled in or out.

Only profiles whose role contains `ReadOnly` are listed as read-only keys unless you tick **All profiles** in Add targets (or the same switch in Settings → General); other roles are marked with a warning.

## Hotkeys

| Hotkey | Status on this machine when checked |
|---|---|
| **Win+Alt+A** (default) | free |
| Ctrl+Alt+Shift+A | free |
| Win+Alt+Q | free |

You can change the hotkey in Settings → General. Press the combination in the box and it tells you whether the combo is free. Avoid Ctrl+Shift+letter, because IDEs and browsers use those.

## Security model

- **Read-only by default.** Every SDK client runs through `ReadOnlyGuardHandler` ([src/Skypeek.Aws/ReadOnlyGuard.cs](src/Skypeek.Aws/ReadOnlyGuard.cs)).
  - It rejects any request type that isn't on an exact allowlist of read operations (`List*`/`Describe*`/`Get*`/`Filter*`/`Retrieve*`, plus `DownloadDBLogFilePortion`, which reads an RDS log file), *before* signing or sending.
  - The few non-read actions (see [Non-read actions](#non-read-actions)) pass only for an elevated, user-approved call with the expected narrow parameters.
  - Tests send real write requests (`PutParameter`, `UpdateService`, `RequestEnvironmentInfo`, `StopInstances`, `RevokeSecurityGroupIngress`) through the pipeline and assert that nothing reaches the network unless the call is elevated, approved and narrowly shaped.
  - An architecture test fails the build if the source references any SDK request type that isn't on one of the two lists.
- **Encryption.** Everything local lives in one SQLCipher database at `%LOCALAPPDATA%\Skypeek\vault.db`: settings, targets, secret/parameter lists, health snapshots, network inventory snapshots, the request log and credential halts. The 256-bit key comes from the master password via Argon2id (64 MB, 3 iterations). `vault.meta` next to it holds only the KDF salt and parameters and the hotkey, which is needed before unlock.
- **Values are never stored.**
  - Revealed values hide after 30 seconds.
  - Copied secret values are excluded from Windows clipboard history and cloud sync, and the clipboard is cleared after 30 seconds if it still holds the value.
- **Lock.** After unlocking, the key stays in memory so background refresh keeps running. After the idle lockout (default 15 min) or a Windows lock (Win+L), the UI closes and you must re-enter the master password to get back in.
- **Request log.** It records service, operation, allowlisted parameters (such as the parameter *name* and `WithDecryption`), HTTP status, duration, AWS request ID and error code. It never records values, tokens, headers or response bodies. Entries are kept for 30 days (configurable).

## Expired credentials

When AWS rejects a profile's credentials (`ExpiredToken`, `InvalidClientTokenId`, …), Skypeek:

1. Halts the profile and stores a SHA-256 hash of the rejected `key id | secret | session token`. The halt persists across restarts.
2. Skips every later operation for that profile **without calling AWS**. Skips show as `Skipped` in the request log, and the tray icon turns red.
3. Watches the credentials file (and for SSO profiles the config file and token cache), with a one-minute polling fallback. When that profile's hash changes, it verifies the new credentials with `sts:GetCallerIdentity`, checking that the account matches the profile name, and then resumes and runs any overdue refreshes.

## Tray icon

- **Orange:** everything is OK.
- **Red with a count badge:** at least one problem. Problems are:
  - an EB environment that is failed or degraded
  - an ECS rollout that failed, or a service running below its desired count
  - an RDS database or cluster, or a cache, that is not available or has broken replication
  - an EC2 instance with a failed status check or scheduled maintenance
  - a load balancer target group with unhealthy targets
  - CPU, memory, database connections or storage above a threshold for the sustained period (default 10 min)
  - an active CloudWatch alarm
  - halted credentials
  - a failed sync (lists, health, metrics or network download)

  Hidden resources never count.
- **Grey:** locked since startup.

Warning-level problems turn the icon red by default; you can change that in Settings → General. Toasts appear only when a status changes.

## Notes

- **Unlock after boot.** "Start with Windows" launches Skypeek in the tray. Refresh starts once you unlock it once after login.
- **Secret values and the ReadOnly role.** AWS's managed ReadOnlyAccess policy probably does not grant `secretsmanager:GetSecretValue`, and decrypting a SecureString that uses a customer-managed KMS key needs `kms:Decrypt`. If the role can't read a value, the details view says so.
- **Console links.** Links open the AWS console in your default browser, in whichever account that browser is signed into.
- **CloudWatch cost.** `GetMetricData` costs about $0.01 per 1,000 metrics. Settings shows an estimate for each target; for example, 50 services polled every 5 minutes is roughly $9 per month.
- **EC2 memory.** It needs the CloudWatch agent (`CWAgent` `mem_used_percent`); otherwise it shows `n/a`.

## Troubleshooting and testing

| Variable | Effect |
|---|---|
| `SKYPEEK_HOME` | Uses a different vault folder, e.g. a throwaway test vault |
| `AWS_SHARED_CREDENTIALS_FILE` | Uses a different credentials file (standard AWS variable) |
| `SKYPEEK_DEBUG=1` | Writes crash details to `debug.log` in the vault folder. It's off by default because the file is not encrypted. |

## Layout

```
src/Skypeek.Core      models, CredentialMonitor, TargetScheduler, HealthRules, SearchIndex, services
src/Skypeek.Aws       AwsGateway (typed read-only calls), ReadOnlyGuard, RequestLogHandler, StsValidator
src/Skypeek.Storage   Vault (SQLCipher + Argon2id), VaultRepository
src/Skypeek.App       WPF tray app: search, details, dashboard, logs, network, request log, settings, lock, hotkey
tests/Skypeek.Tests   guard/pipeline, architecture, redaction, credential halting, vault, health rules
```

## Upgrading from AwsManager

Earlier builds were called AwsManager. On first start, Skypeek moves `%LOCALAPPDATA%\AwsManager` to `%LOCALAPPDATA%\Skypeek` (settings, lists and history are kept; the master password is unchanged) and replaces the old "start with Windows" entry. Delete the old `AwsManager.exe` afterwards. The environment variables are now `SKYPEEK_HOME` and `SKYPEEK_DEBUG`.

## Trademarks

Skypeek is an independent project. It is not affiliated with, endorsed by or sponsored by Amazon Web Services. AWS and the names of AWS services are trademarks of Amazon.com, Inc. or its affiliates.
