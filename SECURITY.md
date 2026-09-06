# Política de segurança

## Reportar uma vulnerabilidade

Se encontrares um problema de segurança neste código — escalada de privilégios,
execução de código, um caminho de atualização que possa ser sequestrado, um tweak
que deixe o PC menos seguro — **não abras uma issue pública**.

Usa o separador **Security → Report a vulnerability** deste repositório
(GitHub Private Vulnerability Reporting), ou contacta a equipa pelo Discord da
comunidade.

Compromisso: resposta em 72 horas e correção publicada antes de qualquer divulgação.

## Se és staff de um servidor e suspeitas da aplicação

Antes de banir alguém, [TRANSPARENCIA.md](TRANSPARENCIA.md) responde à maior parte
das perguntas, com a linha de código de cada operação. Em particular:

- **Snap Tap / repetição de teclado** são as únicas funcionalidades que injetam
  input. Vêm desligadas e o estado está em texto simples em
  `%AppData%\AdamsToolkit\snaptap.json` e `keyrepeat.json` — dá para confirmar num
  screenshare sem confiar na palavra de ninguém.
- **Limpeza de ficheiros temporários está desativada** desde 2026-08-26,
  precisamente por causa de uma verificação de PC. O motor está reduzido a stubs
  que não apagam nada (`Core/CleanupEngine.cs`).
- **Não há driver de kernel** desde 2026-08-30 (`Core/HardwareMonitor.cs`).

Se depois disto continuares com dúvidas sobre um comportamento concreto, abre uma
issue a descrevê-lo. Preferimos a pergunta ao ban.

## Alertas de antivírus

O executável ainda não está assinado, por isso o SmartScreen avisa e alguns motores
heurísticos dão falso positivo (`Trojan:Win32/Bearfoos.A!ml` é o mais comum). Não é
um comportamento malicioso, é reputação zero: cada versão tem um hash novo.

Falsos positivos são submetidos aos fabricantes de antivírus. Se apanhares um que
ainda não esteja reportado, abre uma issue com o nome do motor e da deteção.

## Âmbito

Este repositório é o **cliente**. O serviço que valida os códigos de acesso é
separado e não está aqui — problemas nesse serviço também devem ser reportados em
privado pelas vias acima.
