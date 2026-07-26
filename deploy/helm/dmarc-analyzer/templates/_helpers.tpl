{{- define "dmarc.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dmarc.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "dmarc.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "dmarc.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "dmarc.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "dmarc.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "dmarc.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}

{{- define "dmarc.secretName" -}}
{{- default (printf "%s-secrets" (include "dmarc.fullname" .)) .Values.auth.existingSecret -}}
{{- end -}}

{{- define "dmarc.postgresHost" -}}
{{- printf "%s-postgres" (include "dmarc.fullname" .) -}}
{{- end -}}

{{/*
Migration strategy. Empty means choose by whether the database is bundled — a
pre-install hook runs before the bundled StatefulSet exists, so on a first
install a Job would wait for a database that is not there yet.
*/}}
{{- define "dmarc.migrationStrategy" -}}
{{- if .Values.migrations.strategy -}}
{{- .Values.migrations.strategy -}}
{{- else if .Values.postgres.enabled -}}
startup
{{- else -}}
job
{{- end -}}
{{- end -}}

{{/*
Connection string, minus the password — that arrives from the Secret through
PGPASSWORD-style substitution in the container env below.
*/}}
{{- define "dmarc.connectionStringPrefix" -}}
{{- if .Values.postgres.enabled -}}
Host={{ include "dmarc.postgresHost" . }};Port=5432;Database={{ .Values.postgres.database }};Username={{ .Values.postgres.username }}
{{- else -}}
Host={{ .Values.externalDatabase.host }};Port={{ .Values.externalDatabase.port }};Database={{ .Values.externalDatabase.database }};Username={{ .Values.externalDatabase.username }}
{{- end -}}
{{- end -}}

{{/*
Environment shared by every pod that talks to the database. The connection
string is assembled in the container from DMARC_DB_PASSWORD so the password
never appears in a manifest, only in the Secret.
*/}}
{{- define "dmarc.env" -}}
- name: DMARC_DB_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "dmarc.secretName" . }}
      key: db-password
- name: Security__CredentialEncryptionKey
  valueFrom:
    secretKeyRef:
      name: {{ include "dmarc.secretName" . }}
      key: encryption-key
{{- if and (not .Values.postgres.enabled) .Values.externalDatabase.connectionString }}
- name: ConnectionStrings__Default
  value: {{ .Values.externalDatabase.connectionString | quote }}
{{- else }}
- name: ConnectionStrings__Default
  value: "{{ include "dmarc.connectionStringPrefix" . }};Password=$(DMARC_DB_PASSWORD)"
{{- end }}
{{- range $key, $value := .Values.extraEnv }}
- name: {{ $key }}
  value: {{ $value | quote }}
{{- end }}
{{- range $key, $ref := .Values.extraEnvFromSecret }}
- name: {{ $key }}
  valueFrom:
    secretKeyRef:
      name: {{ $ref.name }}
      key: {{ $ref.key }}
{{- end }}
{{- end -}}

{{/*
Guardrails. Each of these is a configuration that would deploy successfully and
then misbehave in a way that is hard to attribute, so the chart refuses it at
template time instead.
*/}}
{{- define "dmarc.validate" -}}
{{- if not (has .Values.mode (list "combined" "split")) -}}
{{- fail (printf "mode must be \"combined\" or \"split\", got %q" .Values.mode) -}}
{{- end -}}

{{- if and (eq .Values.mode "combined") (gt (int .Values.app.replicas) 1) -}}
{{- fail (printf "mode=combined runs the ingestion loop inside every app pod, so app.replicas=%d would start %d schedulers claiming the same mailboxes: duplicate IMAP sessions and duplicate sync runs. Use mode=split to scale the console, or set app.replicas=1." (int .Values.app.replicas) (int .Values.app.replicas)) -}}
{{- end -}}

{{- if ne (int .Values.worker.replicas) 1 -}}
{{- fail (printf "worker.replicas must be 1 (got %d). Whether two workers can claim from the queue concurrently is unverified, so the chart will not imply a guarantee the queue may not make." (int .Values.worker.replicas)) -}}
{{- end -}}

{{- if and (not .Values.postgres.enabled) (not .Values.externalDatabase.host) (not .Values.externalDatabase.connectionString) -}}
{{- fail "postgres.enabled is false, so set externalDatabase.host (or externalDatabase.connectionString)." -}}
{{- end -}}

{{- if and (not .Values.auth.encryptionKey) (not .Values.auth.existingSecret) -}}
{{- fail "Set auth.encryptionKey (openssl rand -base64 32) or auth.existingSecret. Without it mailbox credentials are stored in plaintext." -}}
{{- end -}}

{{- if not (has (include "dmarc.migrationStrategy" .) (list "job" "startup")) -}}
{{- fail (printf "migrations.strategy must be \"job\", \"startup\" or \"\", got %q" .Values.migrations.strategy) -}}
{{- end -}}

{{- if and (eq (include "dmarc.migrationStrategy" .) "startup") (gt (int .Values.app.replicas) 1) -}}
{{- fail (printf "migrations.strategy=startup with app.replicas=%d lets replicas race to apply the same migration. Use migrations.strategy=job." (int .Values.app.replicas)) -}}
{{- end -}}
{{- end -}}

{{/*
Wait for the database to accept TCP connections.

Kubernetes has no equivalent of Compose's `depends_on: condition: service_healthy`
— every pod in a release starts at once. Without this the app comes up before the
Postgres Service resolves, throws "Name or service not known", and is restarted
until the database happens to be ready. It does converge, but it looks like a
crash-loop, and on a slower cluster the restart backoff turns a 20-second wait
into several minutes.

Uses the application image and bash's /dev/tcp rather than pulling a second
image for one connectivity check.
*/}}
{{- define "dmarc.waitForDatabase" -}}
{{- $host := ternary (include "dmarc.postgresHost" .) .Values.externalDatabase.host .Values.postgres.enabled -}}
{{- $port := ternary "5432" (toString .Values.externalDatabase.port) .Values.postgres.enabled -}}
{{- if $host }}
initContainers:
- name: wait-for-database
  image: {{ include "dmarc.image" . }}
  imagePullPolicy: {{ .Values.image.pullPolicy }}
  securityContext:
    {{- toYaml .Values.securityContext | nindent 4 }}
  command:
    - /bin/bash
    - -c
    - |
      until (echo > /dev/tcp/{{ $host }}/{{ $port }}) 2>/dev/null; do
        echo "waiting for {{ $host }}:{{ $port }}"
        sleep 2
      done
      echo "{{ $host }}:{{ $port }} is accepting connections"
{{- end }}
{{- end -}}
