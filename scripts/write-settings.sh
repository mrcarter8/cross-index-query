#!/bin/sh
# Writes appsettings.Development.json from the azd environment.
#
# Run automatically by `azd up` as a postprovision hook. Safe to run by hand at any time; it only
# reads azd environment values and rewrites one gitignored file.

set -eu

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
settings_path="$repo_root/appsettings.Development.json"

require() {
    value="$(azd env get-value "$1" 2>/dev/null || true)"
    case "$value" in
        ''|ERROR*)
            echo "Could not read '$1' from the azd environment." >&2
            echo "Run 'azd up' first, or edit $settings_path by hand." >&2
            exit 1
            ;;
    esac
    printf '%s' "$value"
}

search_endpoint="$(require SEARCH_ENDPOINT)"
openai_endpoint="$(require AZURE_OPENAI_ENDPOINT)"
embedding_deployment="$(require AZURE_OPENAI_EMBEDDING_DEPLOYMENT)"
embedding_model="$(require AZURE_OPENAI_EMBEDDING_MODEL)"
embedding_dimensions="$(require AZURE_OPENAI_EMBEDDING_DIMENSIONS)"
blurb_deployment="$(require AZURE_OPENAI_BLURB_DEPLOYMENT)"

cat > "$settings_path" <<EOF
{
  "Search": {
    "Endpoint": "$search_endpoint"
  },
  "Embedding": {
    "Endpoint": "$openai_endpoint",
    "Deployment": "$embedding_deployment",
    "ModelName": "$embedding_model",
    "Dimensions": $embedding_dimensions,
    "BlurbDeployment": "$blurb_deployment"
  }
}
EOF

echo "Wrote $settings_path"
echo "  search    $search_endpoint"
echo "  openai    $openai_endpoint"
echo ''
echo 'Next: dotnet run --project src/CrossIndexQuery.Cli -- doctor'
