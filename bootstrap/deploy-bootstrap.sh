#!/bin/bash
# Publica o bootstrapper (AdamsToolkitSetup.exe) — o exe que instala o .NET em
# silêncio e abre o Adams Toolkit. Self-contained (corre sem .NET no PC).
# Coloca em /var/www/adams-toolkit/AdamsToolkitSetup.exe (servido pelo Caddy).
set -e
cd "$(dirname "$0")"

# EnableCompressionInSingleFile=false de propósito: um single-file self-contained
# COMPRIMIDO tem exatamente a forma de um exe "packed", e o motor de ML do Defender
# usa isso como sinal (falso positivo Trojan:Win32/Bearfoos.A!ml, 2026-09-01).
# Sem compressão são +2,7 MB (11,6 -> 14,3 MB) e um PE de layout normal. Vale a troca.
/root/.dotnet/dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=false -o out

DEST=/var/www/adams-toolkit/AdamsToolkitSetup.exe
cp out/AdamsToolkitSetup.exe "$DEST"
chmod a+rX "$DEST"
echo "== setup publicado"
echo "   sha256: $(sha256sum "$DEST" | cut -d' ' -f1)"
echo "   https://adams-176-111-109-91.sslip.io/files/AdamsToolkitSetup.exe"
