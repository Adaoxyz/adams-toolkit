#!/bin/bash
# Publica o Adams Toolkit e ativa o auto-update:
# 1. dotnet publish  2. copia o exe para /var/www/adams-toolkit  3. escreve version.json
# Para lançar versão nova: sobe <Version> no AdamsToolkit.csproj e corre ./deploy.sh —
# todas as apps instaladas atualizam-se sozinhas no próximo arranque.
set -e
cd "$(dirname "$0")"

VER=$(grep -oP '<Version>\K[^<]+' AdamsToolkit.csproj)
DEST=/var/www/adams-toolkit
URL="https://adams-176-111-109-91.sslip.io/files/AdamsToolkit.exe"

# framework-dependent (config estável, 4.5MB). NOTA: self-contained (v1.28.0) fez
# a app não abrir para ninguém — exe 60MB rebentava em silêncio (packaging/AV).
# O .NET-em-falta no 1º arranque resolve-se com bootstrapper à parte, não aqui.
# --- 1) EXE single-file (LEGADO) -------------------------------------------
# Só existe para os clientes <= 1.90.0 continuarem a auto-atualizar: o SelfUpdater
# antigo escreve o que vier de "url" por cima do próprio exe, por isso "url" TEM de
# continuar a servir um exe. Clientes novos ignoram-no e usam o zip.
/root/.dotnet/dotnet publish -c Release -r win-x64 --self-contained false \
  /p:PublishSingleFile=true -o publish-single

mkdir -p "$DEST"
cp "publish-single/Adams Toolkit.exe" "$DEST/AdamsToolkit.exe"

# --- 2) PASTA + ZIP (formato atual) ----------------------------------------
# O bundle single-file tem a forma de um exe "packed" e a nuvem da Microsoft
# classificava-o como malicioso (erro 4556 no arranque, 02-09-2026). Uma pasta
# normal com exe + DLLs não tem essa forma. Mesmo motivo que obrigou a desligar a
# compressão no bootstrapper a 01-09.
rm -rf publish-folder
/root/.dotnet/dotnet publish -c Release -r win-x64 --self-contained false \
  /p:PublishSingleFile=false -o publish-folder

python3 - "$DEST/AdamsToolkit.zip" publish-folder <<'PYZIP'
import os, sys, zipfile
dest, src = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dest, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in files:
            if f.endswith('.pdb'):   # símbolos de debug não vão para os jogadores
                continue
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, src))
print("zip:", dest, os.path.getsize(dest), "bytes")
PYZIP

# Assinatura digital (Authenticode) — ativa sozinha quando o certificado existir.
# Coloca o .pfx em /root/codesign/adams.pfx e a password em /root/codesign/adams.pass
# (chmod 600 nos dois). Sem certificado, o deploy segue sem assinar.
PFX=/root/codesign/adams.pfx
PASSFILE=/root/codesign/adams.pass
if [[ -f "$PFX" && -f "$PASSFILE" ]]; then
  echo "== a assinar exe (Authenticode + timestamp)"
  osslsigncode sign -pkcs12 "$PFX" -readpass "$PASSFILE" \
    -n "Adams Toolkit" -i "https://adams-176-111-109-91.sslip.io" \
    -t http://timestamp.digicert.com -h sha256 \
    -in "$DEST/AdamsToolkit.exe" -out "$DEST/AdamsToolkit.signed.exe"
  mv "$DEST/AdamsToolkit.signed.exe" "$DEST/AdamsToolkit.exe"
  osslsigncode verify -in "$DEST/AdamsToolkit.exe" | grep -E 'Signature verification|Subject' || true
else
  echo "== AVISO: exe NÃO assinado (sem /root/codesign/adams.pfx) — SmartScreen vai avisar os jogadores"
fi

SHA=$(sha256sum "$DEST/AdamsToolkit.exe" | cut -d' ' -f1)
ZIPSHA=$(sha256sum "$DEST/AdamsToolkit.zip" | cut -d' ' -f1)
ZIPURL="${URL%/AdamsToolkit.exe}/AdamsToolkit.zip"

# url/sha256 = exe legado (clientes antigos); zip/zipSha256 = formato atual.
cat > "$DEST/version.json" <<EOF
{"version":"$VER","url":"$URL","sha256":"$SHA","zip":"$ZIPURL","zipSha256":"$ZIPSHA"}
EOF
chmod -R a+rX "$DEST"

echo "== deploy OK: v$VER"
echo "   exe sha256: $SHA"
echo "   zip sha256: $ZIPSHA"
echo "   $URL"
