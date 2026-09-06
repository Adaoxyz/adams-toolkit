# Adams Toolkit

Aplicação de Windows que junta num sítio só o que um jogador de FiveM precisa:
instalar launchers e dependências, ver e atualizar drivers, afinar desempenho e
latência, limpar processos de fundo, ver as especificações do PC e editar clips.

WPF / .NET 8 · Windows 10 e 11 · Português.

> **Staff de servidores e verificações de PC:** ler
> **[TRANSPARENCIA.md](TRANSPARENCIA.md)**. Diz exatamente o que a app faz, o que
> não faz, e a linha de código de cada operação privilegiada.

## Porque é que este código está público

A aplicação não está assinada com um certificado Authenticode. Em scanners e
screenshares aparece como "editor desconhecido" e já custou bans a jogadores que
não fizeram nada de errado. Não há forma de responder a isso com promessas — só
com o código à vista. Está aqui tudo o que é compilado no binário distribuído.

## Funcionalidades

| Secção | O que faz |
|---|---|
| **Painel** | Notícias, estado da comunidade, temperaturas e uso do sistema |
| **Instalar** | Steam, Rockstar, Epic, EA, Ubisoft, Battle.net, GOG, ferramentas de voz — sempre a partir dos **links oficiais** de cada fabricante |
| **Atualizações** | Drivers via Windows Update (WUA COM) e NVIDIA |
| **Otimizador** | Plano de energia, prioridades, tweaks de rede/latência, processos de fundo, serviços opcionais, efeitos visuais, resolução do timer |
| **FiveM Center** | Abrir o jogo e as pastas, cache, ReShade, Snap Tap, repetição de teclado |
| **PC Check** | Relatório das especificações da máquina (só leitura, fica local) |
| **Editor** | Cortar e juntar clips com ffmpeg (NVENC quando disponível) |
| **Desinstalador** | Remove programas e apps da Store pelos desinstaladores oficiais |

Tudo o que altera definições do Windows guarda o valor original em
`%AppData%\AdamsToolkit\` e tem botão para reverter. A maior parte dos tweaks dura
só a sessão de Windows — reiniciar o PC repõe tudo.

## Estrutura

```
AdamsToolkit.csproj        projeto (net8.0-windows, WPF)
App.xaml                   tema (paleta, estilos, animações)
MainWindow.xaml(.cs)       shell: titlebar, sidebar, gate de login
Core/                      toda a lógica (ver TRANSPARENCIA.md para o mapa)
Views/                     um ficheiro por separador da app
bootstrap/                 instalador pequeno que trata do .NET em falta
deploy.sh                  script real de publicação
remote-config.sample.json  exemplo da configuração remota
```

## Compilar

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=false
```

Requisito na máquina do jogador: .NET 8 Desktop Runtime (o `bootstrap/` instala-o
se faltar).

## Configuração remota

A administração faz-se editando **um único JSON alojado por quem corre a app**:
lista de aplicações e links, notícias, estado da comunidade, URL do backend de
acesso. Modelo completo em `remote-config.sample.json`.

A URL vem de `Core/RemoteConfig.cs` → `DefaultConfigUrl`, ou de
`%AppData%\AdamsToolkit\config-url.txt` (sem recompilar).

## Acesso

O acesso é dado por presença num servidor de Discord, através de um código de
emparelhamento de 6 caracteres. **Não usa OAuth** — a app não fica ligada à conta
do jogador nem aparece em "Aplicações autorizadas".

O backend que valida os códigos é um serviço à parte e **não faz parte deste
repositório**: aqui está só o cliente (`Core/DiscordAuth.cs`), que fala com quatro
endpoints (`/toolkit/start`, `/poll`, `/validate`, `/ping`). Quem quiser correr um
fork tem de pôr o seu próprio backend, ou apontar `AuthApiBase` para outro sítio.

Dados que saem do PC: ID de Discord, nome e avatar. Mais nada — ver
[TRANSPARENCIA.md](TRANSPARENCIA.md#o-que-sai-do-pc-para-a-internet).

## Dados locais

`%AppData%\AdamsToolkit\` — cache da configuração, sessão, backups dos valores
originais dos tweaks, histórico de downloads e a pasta `Downloads\`.

## Licença

[GPL-3.0](LICENSE). Podes usar, estudar, alterar e distribuir — **desde que o
resultado continue com a mesma licença e com o código aberto**. Se fizeres um fork,
ele fica público como este.

O nome **"Adams Toolkit"** e os ficheiros de marca em `Assets/` (logo e ícone) não
são abrangidos pela licença: um fork tem de usar nome e imagem próprios. Ver
[TRADEMARK.md](TRADEMARK.md).

## Segurança

[SECURITY.md](SECURITY.md).
