#!/bin/sh
set -eu

# Certificates are supplied locally through compose and are never baked into the image.
# update-ca-certificates safely keeps the public Mozilla roots and adds local trust anchors.
update-ca-certificates

exec dotnet DevScope.Api.dll
