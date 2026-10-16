#!/usr/bin/env bash
# Stores the two secrets .github/workflows/ingest.yml needs, read from .env.production (never printed).
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; source .env.production; set +a
: "${DATABASE_URL:?set DATABASE_URL in .env.production}"
: "${LLM_PRIMARY_API_KEY:?set LLM_PRIMARY_API_KEY in .env.production}"
python3 -I deploy/neon-connection.py "$DATABASE_URL" | gh secret set POSTGRES_CONNECTION
printf %s "$LLM_PRIMARY_API_KEY" | gh secret set LLM_PRIMARY_API_KEY
echo "Secrets set. Run the first ingest with: gh workflow run ingest"
