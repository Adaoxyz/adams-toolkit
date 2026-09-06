# Transparência — para staff de servidores e verificações de PC

Esta página existe por uma razão concreta: o Adams Toolkit aparece em scanners e
verificações de PC como "editor desconhecido", e já houve jogadores banidos por
isso. Este documento diz exatamente o que a aplicação faz, o que **não** faz, e
onde está cada linha de código que faz alguma coisa privilegiada. Todo o código
fonte está neste repositório e o binário distribuído é compilado a partir dele.

**Se és staff e queres verificar uma acusação, começa pela tabela "Operações
privilegiadas" — tem o ficheiro e a linha de cada coisa.**

---

## O que a aplicação NÃO faz

Isto não é uma lista de intenções. Cada item é verificável no código.

| Não faz | Verificação |
|---|---|
| **Não lê nem escreve memória de outro processo** | Não existe um único `ReadProcessMemory`/`WriteProcessMemory`/`OpenProcess` com acesso de memória no repositório. `grep -rn "ProcessMemory\|CreateRemoteThread\|VirtualAllocEx" .` → vazio |
| **Não injeta DLL nem código no FiveM/GTA V** | Sem `CreateRemoteThread`, sem `SetWindowsHookEx` com DLL, sem manifest de injeção |
| **Não instala driver de kernel** | `Core/HardwareMonitor.cs` é um stub sem driver desde v1.87.2. A dependência `LibreHardwareMonitorLib` — que trazia os recursos `WinRing0.gz`/`WinRing0x64.gz` embutidos na DLL — foi **removida do `.csproj`**, portanto não existem bytes de driver no binário. (A palavra "WinRing0" ainda aparece no texto do changelog dentro da app, `Views/ChangelogView.xaml.cs:131`, a explicar essa mesma remoção: é texto de interface, não um driver.) |
| **Não apaga rasto do sistema** | `Core/CleanupEngine.cs` está reduzido a stubs inertes: sem P/Invoke ao `shell32`, sem caminhos `%TEMP%`/WER, não apaga nada. `Core/ScheduledCleanup.cs` tem `IsEnabled => false` fixo e **remove** a tarefa agendada antiga em todos os PCs onde existia |
| **Nunca toca em Prefetch, event logs, USN journal ou Reciclagem** | Regra escrita no cabeçalho de `Core/GameBoost.cs`. Foi por causa de um ban por "limpeza de rasto" (2026-08-26) que a limpeza foi desligada de vez |
| **Não desliga o Windows Defender nem serviços de diagnóstico** | A lista de serviços opcionais (`Core/ProcessTrimmer.cs`) exclui explicitamente antivírus; e só **para** serviços nesta sessão, nunca os desativa |
| **Não altera ficheiros do GTA V / FiveM** | O único ficheiro que a app escreve na pasta do jogo é o ReShade, e só a pedido explícito (`Core/ReshadeInstaller.cs`) |
| **Não faz upload de dados do PC** | Existe **um** `PostAsync` em todo o repositório (`Core/DiscordAuth.cs:59`) e vai sem corpo. Especificações do PC, ficheiros e capturas nunca saem da máquina |
| **Não corre nada escondido em segundo plano** | Sem serviço próprio, sem tarefa agendada ativa. A app fecha e para |

## O que sai do PC para a internet

Lista completa. Não há mais nenhum destino de rede no código:

1. `POST /toolkit/start` — sem corpo, pede um código de emparelhamento.
2. `GET /toolkit/poll?code=…` — pergunta se o código já foi aprovado.
3. `GET /toolkit/validate?token=…` — revalida a sessão no arranque.
4. `GET /toolkit/ping?token=…` — "a app está aberta", para o painel de staff.
5. `GET …/config.json` — descarrega a configuração remota (lista de apps, notícias).
6. Downloads que o jogador pede: os instaladores vêm **dos sites oficiais**
   (Steam, Rockstar, Epic, …), a app só gere os links. Ver `remote-config.sample.json`.

Dados pessoais envolvidos: ID de Discord, nome de utilizador e avatar — e mais nada.
Não há OAuth: a app não fica ligada à conta Discord do jogador nem aparece em
"Aplicações autorizadas".

## Operações privilegiadas (as que um scanner vai apitar)

Todas reversíveis, todas visíveis no registo do Windows, todas com backup do valor
original em `%AppData%\AdamsToolkit\`.

| O quê | Onde | Precisa de UAC | Notas |
|---|---|---|---|
| Hook de teclado `WH_KEYBOARD_LL` | `Core/SnapTap.cs:149`, `Core/KeyboardRepeat.cs:92` | Não | **Ler a secção abaixo — é a única funcionalidade com risco real de ban** |
| Parar processos de fundo | `Core/ProcessTrimmer.cs` | Só para serviços | Lista fechada; `IsProcessCritical` antes de cada `Kill`; nunca mata FiveM, Rockstar, Steam, Discord, browsers, antivírus |
| Parar serviços opcionais (esta sessão) | `Core/ProcessTrimmer.cs:370` | Sim | Só `Stop`, nunca `Disable`, nunca drivers |
| Tweaks de rede/latência | `Core/NetworkOptimizer.cs` | Sim | Backup JSON do valor original; a maioria reverte-se sozinha ao reiniciar |
| Prioridades / plano de energia | `Core/GameBoost.cs` | Sim | Por sessão de Windows; reboot reverte |
| Resolução do timer (0.5 ms) | `Core/TimerResolutionService.cs` | Não | `NtSetTimerResolution` — pedido por processo, morre com a app |
| Atualização de drivers | `Core/DriverUpdateService.cs` | Sim | Windows Update (WUA COM) e NVIDIA. Nada de fontes de terceiros |
| Desinstalar programas | `Core/UninstallerEngine.cs`, `Core/AppxUninstaller.cs` | Sim | Corre o desinstalador oficial de cada programa |
| Ponto de restauro | `Core/RestorePointService.cs` | Sim | Cria, nunca apaga |
| Arranque com o Windows | `Core/StartupManager.cs` | Não | Atalho `.lnk` na pasta Arranque (deixou de usar a chave `Run` em v1.89.0) |
| Auto-atualização | `Core/SelfUpdater.cs` | Não | Descarrega a versão nova do host oficial e substitui-se |

## Snap Tap e Repetição de teclado — leiam isto

`Core/SnapTap.cs` e `Core/KeyboardRepeat.cs` instalam um hook de teclado de baixo
nível, engolem as teclas físicas WASD e re-injetam-nas com `SendInput`. É o
equivalente em software do Razer Snap Tap / Wooting Rappy Snappy.

**Isto é, tecnicamente, um macro de input.** Não escondemos isso: está escrito no
cabeçalho dos dois ficheiros e a interface avisa o jogador antes de ativar.

Três factos que interessam a uma verificação:

1. **Vem desligado e nunca se liga sozinho.** O hook só é instalado se o jogador
   tiver ligado a opção à mão: `RestoreOnBoot() => Cfg.Enabled ? Start() : null`
   (`SnapTap.cs:171`, `KeyboardRepeat.cs:87`). Num PC onde nunca foi ligado, não
   existe hook nenhum.
2. **Só age com o FiveM em primeiro plano** (`FiveMForeground()`), e só nas teclas
   A/D (opcionalmente W/S). Fora do jogo o teclado passa intocado.
3. **Estado guardado em ficheiro** — dá para verificar num screenshare sem confiar
   na palavra de ninguém: `%AppData%\AdamsToolkit\snaptap.json` e `keyrepeat.json`.
   `"Enabled": false` = a funcionalidade nunca correu.

Se o teu servidor proíbe SOCD/null-bind — e é legítimo que proíba — o que deves
verificar é esse ficheiro, não a mera presença da aplicação.

## Compilar e comparar

O formato distribuído hoje é **uma pasta com o exe e as DLLs ao lado**, empacotada
num zip:

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=false
```

Passos completos em [`deploy.sh`](deploy.sh) — é o script real de publicação, não
uma versão limpa para mostrar.

Não há ofuscação, packing nem compressão em passo nenhum, e isso é deliberado:
o bundle single-file tem a forma de um exe "packed" e a nuvem da Microsoft chegou
a classificá-lo como malicioso (erro 4556, 02-09-2026). Pela mesma razão o
bootstrapper leva `EnableCompressionInSingleFile=false`
(`bootstrap/deploy-bootstrap.sh:13`). O exe single-file continua a ser publicado
só para os clientes ≤ 1.90.0 se conseguirem auto-atualizar.

Os símbolos de debug (`.pdb`) são o único ficheiro excluído do zip.

## Antivírus

O executável **não está assinado** com certificado Authenticode, por isso o
SmartScreen e alguns motores heurísticos marcam-no como desconhecido
(`Trojan:Win32/Bearfoos.A!ml` é o falso positivo mais comum). É um problema de
reputação, não de comportamento: cada versão nova tem um hash novo e a reputação
volta a zero.

Publicar o código aqui é o primeiro passo para o resolver. O `deploy.sh` já assina
automaticamente quando existir um certificado.

## Reportar um problema

Ver [SECURITY.md](SECURITY.md).
