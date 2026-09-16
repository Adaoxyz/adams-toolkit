using System.Windows;
using System.Windows.Controls;

namespace AdamsToolkit.Views;

public record Release(string Version, string Date, string Title, List<string> Points)
{
    // a 1ª entrada da lista é a versão atual
    public Visibility CurrentVis { get; init; } = Visibility.Collapsed;
}

/// <summary>
/// "Novidades": changelog embutido na app — cada release acrescenta a sua entrada
/// aqui (linguagem simples, sem tecniquês; o detalhe técnico vive no git/memória).
/// </summary>
public partial class ChangelogView : UserControl
{
    private static readonly List<Release> Releases = new()
    {
        new("v1.99", "16 set 2026", "Mudança de servidor",
        new()
        {
            "A app passou a ligar-se ao nosso servidor novo. O antigo vai ser desligado em breve — esta atualização garante que o login, as atualizações automáticas, o FFmpeg e o ReShade continuam a funcionar.",
            "O teste de ping e a prioridade QoS do Boost de rede apontam agora para o IP novo do servidor.",
            "Não tens de fazer nada: continua tudo igual do teu lado.",
        }),
        new("v1.98", "16 set 2026", "Código aberto mudou para o GitLab",
        new()
        {
            "O botão «Abrir no GitLab» na secção Código Aberto leva agora para gitlab.com/adamstoolkit-group/adams-toolkit — o link antigo do GitHub deixou de funcionar.",
            "Continua tudo público: qualquer pessoa pode ler o código e comparar com o programa que está a correr.",
        }),
        new("v1.97", "9 set 2026", "Editor de Clips sem lag em ecrã inteiro",
        new()
        {
            "O vídeo do editor deixou de engasgar quando a janela está maximizada. A janela do editor era desenhada de uma forma que obrigava o Windows a copiar a imagem toda a cada frame; agora o vídeo vai direto para a placa gráfica.",
            "Os cantos redondos e o redimensionar da janela continuam iguais (no Windows 10 os cantos ficam direitos, só isso).",
        }),
        new("v1.96", "7 set 2026", "Snap Tap removido de vez",
        new()
        {
            "O Snap Tap (strafe) saiu da app. Deixou de existir o card no FiveM Center, o programa já não instala o atalho de teclado que essa função usava e, ao abrir esta versão, apaga sozinho o ficheiro onde a tua escolha estava guardada (snaptap.json).",
            "Se o tinhas ligado, fica desligado a partir de agora sem teres de fazer nada — nem sequer é preciso ir a lado nenhum.",
            "A secção «Código Aberto» passa a mostrar só a repetição de teclado, que é o que resta a mexer no input. O resto da app fica igual.",
        }),
        new("v1.95", "6 set 2026", "O código da app passou a ser público",
        new()
        {
            "Nova secção «Código Aberto» na barra lateral: o código completo do Adams Toolkit está publicado no GitHub e podes abri-lo com um clique.",
            "Serve para as verificações de PC. Em vez de discutir, mostras a secção: quem está a verificar clica, lê o código e vê o que a app faz e não faz — não lê a memória de outros programas, não injeta nada no jogo, não instala drivers e não apaga rasto nenhum do teu PC.",
            "A mesma secção mostra, ao vivo, se tens o Snap Tap e a repetição de teclado ligados ou desligados, e abre a pasta dos ficheiros que guardam esse estado, para a staff confirmar sem ter de acreditar na app.",
        }),
        new("v1.94", "6 set 2026", "O «Reparar» do PC check repara mesmo",
        new()
        {
            "O botão «Reparar» do «Pronto para PC check?» ficava a olhar quando um serviço estava DESATIVADO (Windows Search, Agendador de Tarefas…): agora liga-os pelo caminho certo do Windows e eles ficam mesmo a correr, e a arrancar sozinhos no próximo reboot.",
            "Deixou de reparar em silêncio: no fim diz-te o que tratou e, do que não deu, explica porquê em vez de mostrar a mesma lista de problemas.",
            "Windows Defender parado: a app tira as políticas que o desligam e, se o Windows continuar a bloquear, diz-te o passo que falta (desligar a Proteção contra Adulteração ou tirar o antivírus de terceiros) em vez de falhar sem dizer nada.",
        }),
        new("v1.93", "3 set 2026", "Menos abas: saiu «Atualizações»",
        new()
        {
            "Tirámos a aba «Atualizações» — não fazia falta e o Windows já atualiza os drivers sozinho. De caminho, a app deixou de fazer a pesquisa de drivers ao abrir (era a parte mais lenta), por isso arranca mais rápido.",
        }),
        new("v1.92", "3 set 2026", "Mais leve para verificações de PC",
        new()
        {
            "Limpámos do programa restos antigos da antiga «Limpeza do PC» (já estava desligada há tempo). Assim nenhum screenshare ou anti-cheat te confunde com um limpa-registos por falso positivo. A app não apaga nada do teu PC.",
        }),
        new("v1.83", "29 ago 2026", "Pronto para PC check?",
        new()
        {
            "Novo no FiveM Center → Manutenção: «Pronto para PC check?». Verifica que tudo o que a staff lê está ativo e intacto — Prefetch, serviços (SysMain, EventLog, DiagTrack, PcaSvc, DPS, Windows Search, Defender…), registos de eventos sem limpezas nos últimos 30 dias, BAM, itens recentes, histórico de atividade, USN journal (como admin) e se há ferramentas de limpeza instaladas.",
            "Mostra ✅/⚠️/❌ com explicação simples. «Reparar» liga serviços e repõe o registry do Prefetch/Timeline — nunca apaga nada. O que já foi limpo não se desfaz: o aviso serve para não repetires.",
        }),
        new("v1.82.3", "29 ago 2026", "Editor: fechar clip + atalho de exportar",
        new()
        {
            "Botão «✕ Fechar clip» no topo: tira o clip do editor (o ficheiro fica onde está). Atalho Ctrl+Shift+S = Exportar. Ícone ▶/⏸ suave no centro do vídeo ao dar play/pausa.",
        }),
        new("v1.82", "29 ago 2026", "Editor: arrasto instantâneo estilo Avidemux",
        new()
        {
            "Ao abrir um clip, o arrasto fica utilizável em 2-3 segundos: primeiro lê só os keyframes (como o Avidemux faz — 1 imagem a cada ~2 s de vídeo), e vai enchendo o detalhe frame a frame em fundo. Enquanto uma zona não tem detalhe, arrastar mostra o keyframe mais próximo; depois passa a ser frame-exato.",
        }),
        new("v1.81", "29 ago 2026", "Editor: reprodução e arrasto na mesma qualidade (720p)",
        new()
        {
            "O 1080p no arrasto dava problemas em alguns PCs. Agora é tudo 720p: a cache de arrasto (60 fps) e uma versão 720p do clip para reprodução, gerada em paralelo logo a seguir — a troca entre arrastar e ver o vídeo a correr fica sem diferença visível. Até essa versão estar pronta, toca o ficheiro original. A exportação usa sempre o original.",
        }),
        new("v1.80", "29 ago 2026", "Editor: arrasto em 1080p e pronto em segundos",
        new()
        {
            "A imagem do arrasto é agora 1080p (a mesma qualidade do vídeo) — a troca para o vídeo a correr deixa de se notar.",
            "A preparação passou a ser feita em paralelo (o clip é partido em vários pedaços e cada um é gerado ao mesmo tempo): num PC de 8 núcleos, um clip de 4-5 minutos fica todo pronto em cerca de 15-20 segundos, e o arrasto já funciona nos primeiros segundos. Ocupa mais cache em disco (limpa-se sozinha).",
        }),
        new("v1.79.1", "29 ago 2026", "Editor: leitor de volta ao do Windows",
        new()
        {
            "A v1.79 experimentou um leitor de vídeo novo para tirar a pequena paragem depois de arrastar — mas estragava o som. Voltou o leitor do Windows (som certo); a paragem curta ao dar Play depois de um salto ficou, disfarçada pela ponte + fade.",
            "O motor de vídeo (60 MB) descarregado na v1.79 continua a ser usado para o arrasto e a exportação — não volta a descarregar.",
        }),
        new("v1.78", "29 ago 2026", "Editor: pronto em segundos",
        new()
        {
            "Acabou o «a preparar…» de minutos. A reprodução usa o teu ficheiro original (qualidade toda, sem conversão). O arrasto instantâneo fica disponível poucos segundos depois de abrir — primeiro no início do clip e vai avançando em fundo (vês «pronto até mm:ss» no painel Exportar). Onde ainda não chegou, o arrasto funciona na mesma, só menos fluido.",
            "A geração usa a placa gráfica para descodificar; num clip de 4-5 minutos a 120 fps fica completa em menos de 1 minuto e fica em cache para a próxima vez.",
        }),
        new("v1.77", "29 ago 2026", "Editor: pré-visualização em 1080p",
        new()
        {
            "A pré-visualização fluida passou de 540p para 1080p (ou a resolução do teu clip, se for menor) e o arrasto na timeline de 270p para 540p — vês bem o que estás a cortar. Continua suave; só ocupa mais espaço em cache (limpa-se sozinha ao fim de 3 dias ou acima de 8 GB).",
        }),
        new("v1.76", "29 ago 2026", "Atualização automática antes de tudo",
        new()
        {
            "Ao abrir o programa, a verificação de atualizações acontece logo no início (cartão pequeno «A verificar atualizações…»), antes do ecrã de escolha. Quem for direto ao Editor de Clips também fica sempre na versão mais recente.",
        }),
        new("v1.75", "29 ago 2026", "Editor: arrasto a 60 Hz",
        new()
        {
            "Enquanto arrastas na timeline (ou no slider) o editor já não pede ao leitor de vídeo do Windows para saltar. Depois de abrir o clip prepara, em segundo plano, uma cache de arrasto (todos os frames a 60 por segundo, tamanho pequeno) e a partir daí cada movimento do rato mostra o frame certo na hora — contínuo, sem parecer uma sequência de prints. Ao largar, o vídeo fica nesse ponto.",
            "Ordem ao abrir um clip: 1) vídeo original, 2) «pré-visualização fluida pronta» (reprodução suave), 3) «arrasto instantâneo pronto». Num clip de 4-5 minutos leva cerca de 1 minuto no total; nas vezes seguintes é imediato (fica em cache 7 dias).",
        }),
        new("v1.73", "29 ago 2026", "Editor: pré-visualização fluida",
        new()
        {
            "Ao abrir um clip, o editor prepara em segundo plano uma versão leve só para ver (540p, todos os frames independentes). Assim que fica pronta, arrastar na timeline e andar frame a frame passa a ser instantâneo — sem engasgos. A exportação usa sempre o ficheiro original, na qualidade toda.",
            "Fica guardada em cache: abrir o mesmo clip outra vez é imediato. Limpa-se sozinha ao fim de 7 dias.",
        }),
        new("v1.72", "29 ago 2026", "Editor: tira de miniaturas na timeline",
        new()
        {
            "A timeline do editor mostra agora uma tira de imagens do clip (gerada em segundos, em segundo plano). Vês logo onde está cada momento e podes arrastar em cima da tira — mesma coisa que a barra.",
        }),
        new("v1.71.4", "29 ago 2026", "Editor: arrastar na timeline mais fluido",
        new()
        {
            "Ao arrastar na barra do tempo podes sair com o rato para fora da barra que continua a deslizar (largar = pára). A pré-visualização acompanha o rato mais suave — antes engasgava a cada pixel.",
        }),
        new("v1.71", "29 ago 2026", "Editor de Clips dentro do Toolkit",
        new()
        {
            "O Adams Clips deixou de ser um programa à parte. O editor de clips (cortar, marcar troços, juntar, exportar, GIF, foto do frame) passou para dentro do Adams Toolkit — 1 download, 1 atualização, 1 ícone. A parte de gravação saiu: grava com o OBS, Medal ou o que usares, e edita aqui.",
            "Ao abrir o programa à mão aparecem 2 cartões: \"Adams Toolkit\" ou \"Editor de Clips\". Pergunta de cada vez que abres. O arranque com o Windows continua a ir direto para o Toolkit na bandeja.",
            "O editor abre numa janela própria e leve — só carrega quando o pedes. Na primeira vez descarrega o motor de vídeo (56 MB); quem já tinha o Adams Clips não descarrega nada. Também dá para abrir um .mp4/.mkv com \"Abrir com → Adams Toolkit\" ou arrastar clips para a janela.",
            "Novo item na barra lateral: \"Editor de Clips\". No editor há um botão \"Abrir o Toolkit\" para saltar para o outro lado.",
        }),
        new("v1.70", "29 ago 2026", "Otimizador por separadores",
        new()
        {
            "O Otimizador deixou de ser uma página comprida com secções a abrir e fechar. Agora tem 3 separadores no topo — Boost de Jogo, Manutenção e Arranque do Windows — e só vês a área que escolheres.",
            "O mesmo no FiveM Center: separadores Jogo · Manutenção · Links úteis, com a barra de estado do FiveM sempre fixa por cima.",
        }),
        new("v1.69", "29 ago 2026", "Efeitos visuais: abre a janela do Windows",
        new()
        {
            "O card \"Efeitos visuais do Windows\" deixou de aplicar presets à força. O botão \"Melhor desempenho\" desligava as animações e, em alguns PCs com Windows 11, a caixa de pesquisa da barra de tarefas ficava toda preta ao escrever. Agora há só um botão que abre a janela oficial do Windows e escolhes tu.",
            "Se a tua pesquisa já está preta: abre essa janela e escolhe \"O Windows decide\" (ou liga \"Animar controlos e elementos dentro das janelas\").",
        }),
        new("v1.68.1", "28 ago 2026", "Sem driver de kernel (alerta do Defender)",
        new()
        {
            "O Windows Defender passou a apagar o ficheiro AdamsToolkit.sys com um alerta \"Grave\" (VulnerableDriver:WinNT/Winring0). Era o driver que a biblioteca de sensores usava, só em modo admin, para ler a temperatura do CPU. Fora: a app deixa de extrair qualquer driver.",
            "Consequência: a temperatura da GPU continua igual; a do CPU só aparece nos PCs em que o Windows a expõe sem driver (senão fica N/D). O alerta do Defender não volta a aparecer.",
        }),
        new("v1.68", "28 ago 2026", "Snap Tap / repetição: tecla presa corrigida",
        new()
        {
            "Corrigido: com a \"Repetição do teclado W/A/S/D\" ligada, ao largar uma tecla o boneco às vezes continuava a andar sozinho durante uns segundos (ex.: A → D e ficava a ir para a direita). Uma repetição injetada chegava depois de soltares a tecla e o jogo ficava com ela presa. Já não acontece.",
            "Snap Tap: ao desligar, qualquer tecla que estivesse re-premida por nós é solta no jogo. Mais robusto a alt-tab a meio de um strafe.",
        }),
        new("v1.67", "26 ago 2026", "Limpeza do PC removida",
        new()
        {
            "A \"Limpeza do PC\" foi DESATIVADA e a limpeza semanal automática desligada em todos os PCs. Motivo: um jogador foi banido de um servidor por \"limpeza de logs\" depois de usar esta função.",
            "Para ficar claro: a limpeza NUNCA tocou no Prefetch (era excluído de propósito). Só apagava ficheiros temporários (do utilizador e do Windows), relatórios de erros antigos e esvaziava a Reciclagem — o mesmo que a Limpeza de Disco do próprio Windows faz.",
            "Vamos resolver esta situação diretamente com a staff do servidor em questão. Até lá a função fica desligada. O resto da app (Boost de FPS, ping, timer, repetição do teclado, Snap Tap) não apaga nada do PC e continua igual.",
        }),
        new("v1.66", "26 ago 2026", "Repetição do teclado W/A/S/D + timer automático",
        new()
        {
            "FiveM Center: a \"Repetição do teclado\" passou a ser só nas teclas W, A, S e D — o resto do teclado e o Windows ficam intocados. Só com o FiveM em foco; Repor desliga e pronto.",
            "Timer Resolution: liga-se sozinho (0.5 ms) sempre que a app está aberta, incluindo na bandeja. Fecha a app e volta ao normal.",
        }),
        new("v1.65", "26 ago 2026", "Repetição do teclado em ms",
        new()
        {
            "Novo no FiveM Center: \"Repetição do teclado (ms)\" — dois sliders para o atraso inicial e o intervalo de repetição. O Windows só deixa ir a ~33 ms; aqui desces até 10 ms. Aplica na hora, sem admin, e o Repor padrão devolve tudo.",
            "Armazenamento: 1 clique entra na pasta, ← Anterior volta, ⬆ Subir sobe, e o caminho no topo é clicável.",
        }),
        new("v1.64", "26 ago 2026", "Desinstalador novo + Armazenamento",
        new()
        {
            "Desinstalador redesenhado: barra com separadores, pesquisa e atualizar; linhas em cartões com ícone, versão e tamanho; barra de ação destacada.",
            "Novo separador \"Armazenamento\": escolhe o disco e vê as pastas que ocupam mais espaço (maiores primeiro, com barra e %). Duplo clique entra na pasta, ⬆ volta, abre no Explorador. Só lê — não apaga nada.",
            "Apps do Windows: lista só o que dá para remover; ao remover confirma com o Windows e atualiza na hora.",
        }),
        new("v1.62", "26 ago 2026", "Desinstalador com apps do Windows",
        new()
        {
            "O Desinstalador tem agora 2 separadores: \"Programas\" (o que instalaste por fora — setups .exe/.msi, com caça aos restos como antes) e \"Apps do Windows\" (Store e pré-instaladas: Xbox, Cortana, Clipchamp, Teams, Notícias, Meteorologia…).",
            "As apps do Windows removem-se com 1 clique, sem admin, só para a tua conta — voltam pela Microsoft Store se precisares. As que fazem parte do sistema (Store, Edge, Definições…) aparecem a cinzento e não se mexem.",
        }),
        new("v1.61", "26 ago 2026", "Efeitos visuais + Snap Tap de volta",
        new()
        {
            "Novo no Boost de Jogo: \"Efeitos visuais do Windows\" — os 3 presets do Win+R → SystemPropertiesPerformance (O Windows decide / Melhor desempenho / Melhor aspeto) com um clique, sem admin.",
            "O Snap Tap (strafe) voltou, a pedido — agora no FiveM Center, secção Jogo. Igual ao de antes: A/D (W/S opcional), só com o FiveM em foco, lembra-se da tua escolha.",
        }),
        new("v1.56.1", "21 ago 2026", "RAM certa no \"O meu PC\"",
        new()
        {
            "Em alguns PCs a BIOS não conta todos os módulos de RAM e a página mostrava menos memória do que a real. Agora confirmamos com o próprio Windows e mostramos o total certo.",
        }),
        new("v1.56", "21 ago 2026", "Entrar sem código",
        new()
        {
            "Acabou o copiar códigos: no primeiro arranque clicas em \"Entrar com Discord\", autorizas no browser com 1 clique e a app entra sozinha.",
            "O código antigo continua disponível como alternativa (para quem preferir o /toolkit no Discord).",
        }),
        new("v1.58", "26 ago 2026", "Menos ruído + fechar processos",
        new()
        {
            "Atualizações: saíram os cards \"Drivers instalados\", \"Fabricantes e ferramentas oficiais\" e \"Dispositivos com problemas\". Fica só o que interessa: Windows Update + aviso de driver NVIDIA novo.",
            "Novo no Boost de Jogo: \"Processos desnecessários\" — analisa o PC e fecha com um clique os programas de fundo que não fazem falta para jogar (OneDrive/Dropbox/iCloud, updaters, Adobe, Teams, Widgets, Xbox Game Bar, launchers de outros jogos, Overwolf, Wallpaper Engine). Lista fechada e segura: nunca toca no Windows, Discord, Steam, browsers, voz ou FiveM. Podes desmarcar o que quiseres manter.",
            "Otimizador: o Snap Tap foi removido.",
            "Install Center e Otimizador: os separadores de categoria deixaram de parecer cards — agora são uma linha limpa com o título, divisória e um botão redondo que acende quando a secção está aberta.",
        }),
        new("v1.55.1", "17 ago 2026", "Otimizador arrumado",
        new()
        {
            "O Otimizador deixou de ser uma página gigante: agora tem 3 secções que abres e fechas — Boost de Jogo, Manutenção & Limpeza e Arranque do Windows.",
            "Os ajustes de rede passaram a viver dentro do card do boost de ping (\"Ver e escolher os ajustes\"), e a lista do Boost de FPS também fica escondida até quereres mexer.",
        }),
        new("v1.55", "17 ago 2026", "Snap Tap para o strafe",
        new()
        {
            "Novo no Otimizador: \"Snap Tap (strafe)\" — ao carregares D com A ainda premida, o A solta-se no mesmo instante (e volta quando largas D). Mudança de direção sem o micro-travão. Só nas letras A/D (W/S opcional) e só com o FiveM em foco.",
            "Fica ligado enquanto a app estiver aberta (a bandeja conta) e lembra-se da tua escolha. Usa só onde for permitido.",
            "Boost de FPS: novo ajuste que desliga os atalhos das Teclas Fixas/Filtro/Alternância — acabou a janela do Windows a roubar o foco a meio do jogo.",
        }),
        new("v1.54", "8 ago 2026", "Serviços do Windows ficam ativos de vez",
        new()
        {
            "Ativar um serviço passou a repor também o arranque automático — deixa de voltar atrás quando reinicias o PC.",
            "Botão \"Ativar todos\": um só pedido de administrador trata de todos os serviços da lista.",
            "Serviço a correr mas sem arranque automático aparece agora como \"Só esta sessão\", para saberes que não aguenta um reinício.",
            "Nova opção \"Manter sempre ativos\": cria uma tarefa que reaplica os serviços em cada arranque do Windows, sem janelas nem pedidos de administrador (para quem usa otimizadores que os voltam a desligar).",
        }),
        new("v1.52.6", "6 ago 2026", "Logo novo",
        new()
        {
            "Novo logo em todo o lado: janela, banner de boas-vindas, ícone da app e ícone do instalador.",
            "O ícone passou a ter todos os tamanhos (barra de tarefas, Alt-Tab, Explorer) — deixa de aparecer esborratado.",
        }),
        new("v1.52.5", "6 ago 2026", "Leve como uma pena",
        new()
        {
            "Com o jogo à frente, a app adormece: os monitores de CPU, GPU, temperaturas e ping param e voltam sozinhos quando abres a janela.",
            "Enquanto está em segundo plano fica com prioridade baixa — nunca tira CPU ao jogo.",
            "Na bandeja larga os sensores e a leitura de temperaturas, ficando praticamente sem consumo.",
            "A procura de atualizações de drivers deixou de correr no arranque do Windows (era a parte mais pesada); corre quando abres a app ou a aba Atualizações.",
            "Leituras de temperatura e de utilização da GPU otimizadas: menos trabalho para os mesmos valores.",
        }),
        new("v1.52.4", "6 ago 2026", "Cartão do ping arrumado",
        new()
        {
            "Corrigido: no painel principal, a etiqueta do estado da ligação estava por cima do anel do ping. Agora fica ao lado e vê-se tudo.",
            "A etiqueta muda de cor conforme a ligação: verde (excelente), amarelo (boa) e vermelho (fraca ou sem ligação).",
        }),
        new("v1.52.3", "6 ago 2026", "Aberto a toda a comunidade",
        new()
        {
            "O Adams Toolkit mudou-se para o Discord “Shame & Adão | Edits” e é grátis para todos os membros — não é preciso tag nem boost.",
            "O download está no canal 🧰┃adams-toolkit: escreve /dtoolkit e segue os passos.",
            "Só funciona enquanto estiveres no servidor: se saíres do Discord, o acesso fecha-se sozinho.",
        }),
        new("v1.52.1", "6 ago 2026", "Logo novo e temperatura da GPU certa",
        new()
        {
            "Logo novo em todo o lado: janela, banner e ícone da app.",
            "Corrigido: a temperatura da GPU aparecia 10 a 15 graus acima da real — estava a mostrar o ponto mais quente da placa em vez do núcleo. Agora mostra o mesmo valor que o Afterburner e o painel da NVIDIA.",
        }),
        new("v1.52", "6 ago 2026", "Visual novo de alto a baixo",
        new()
        {
            "A app tem um visual novo em roxo — menu lateral com ícones, banner de boas-vindas e cartões mais limpos.",
            "O painel principal mostra agora anéis de utilização (GPU, RAM, CPU e disco) com o gráfico dos últimos 60 segundos dentro de cada quadro.",
            "Novo: “Ações rápidas” no fundo do painel — otimizar, limpar, verificar drivers e instalar o FiveM num clique.",
            "O gráfico deixou de ser desenhado a toda a hora: gasta menos CPU e a app fica mais leve.",
        }),
        new("v1.51", "5 ago 2026", "Boost de FPS e diagnóstico de quebras",
        new()
        {
            "Novo: “Boost de FPS (FiveM)” no Otimizador — desliga a gravação em segundo plano, dá prioridade ao jogo no CPU e na GPU, força o FiveM na placa gráfica boa e põe o processo em prioridade alta.",
            "Nada disto apaga seja o que for do teu PC: o Prefetch, o histórico e os registos do Windows ficam intactos, por isso é seguro em verificações de PC.",
            "Tudo reversível num botão e desliga-se sozinho quando reinicias o Windows, tal como o boost de rede.",
            "Novo: “Diagnóstico de quebras” — analisa RAM, ficheiro de paginação, disco onde o FiveM está, tamanho da cache e overlays a roubar frames, e diz o que compensa mesmo mudar.",
        }),
        new("v1.50", "29 jul 2026", "Novidades e apps sempre atualizadas",
        new()
        {
            "A lista de programas do Install Center e as novidades passam a chegar diretamente do servidor WestRP — aparecem apps novas sem precisares de atualizar a app.",
            "Sem internet, a app continua a funcionar como antes.",
        }),
        new("v1.49", "26 jul 2026", "Limpar histórico de dispositivos + Otimizador arrumado",
        new()
        {
            "Novo: “Histórico de dispositivos” no Otimizador — apaga o registo de todas as pens, discos, comandos e monitores que já ligaste ou desligaste do PC (com data e hora).",
            "Os dispositivos continuam a funcionar na mesma: só o histórico desaparece. Precisa da app em modo administrador.",
            "A aba Otimizador foi arrumada por secções (Boost de jogo, Manutenção & limpeza, Arranque do Windows, Ajustes de rede) para encontrares tudo mais depressa.",
        }),
        new("v1.48.2", "25 jul 2026", "TeamSpeak sem Overwolf",
        new()
        {
            "O TeamSpeak deixou de ser instalado em modo automático — esse modo trazia o Overwolf à socapa, sem perguntar.",
            "Agora abre o instalador oficial normal: desmarca a opção do Overwolf durante a instalação e ficas só com o TeamSpeak.",
            "As atualizações do TeamSpeak também deixaram de aparecer na aba Atualizações pelo mesmo motivo — atualiza-o pelo próprio programa.",
        }),
        new("v1.48.1", "23 jul 2026", "Instalação de drivers melhorada",
        new()
        {
            "A instalação de drivers agora mostra o progresso driver a driver — antes parecia parada durante as transferências grandes.",
            "Se um driver falhar, os outros instalam-se na mesma e a app diz quantos ficaram.",
        }),
        new("v1.48", "23 jul 2026", "Atualizações a sério + arranque mais rápido",
        new()
        {
            "A verificação de drivers foi corrigida — antes quase nunca encontrava nada; agora procura no catálogo completo da Microsoft e encontra o que o teu PC realmente precisa.",
            "A aba Atualizações agora também mostra os teus programas com versão nova (Discord, browsers, etc.) — um clique e ficam atualizados a partir das fontes oficiais.",
            "Novo: gere os programas que arrancam com o Windows no Otimizador — desliga os que não precisas e ganha FPS (é reversível, igual ao Gestor de Tarefas).",
            "Novo: ponto de restauro do sistema com um clique, e criado automaticamente antes de instalar drivers — se algo correr mal, voltas atrás.",
            "Novo: limpeza automática semanal (opcional) — o PC limpa-se sozinho ao domingo, sem abrires a app.",
        }),
        new("v1.47", "18 jul 2026", "Funciona mesmo offline",
        new()
        {
            "Se o servidor de verificação estiver em baixo (ou a tua net falhar), a app já não te tranca fora — entras na mesma com o teu último login, durante até 7 dias.",
            "A verificação normal continua igual: quem perder a tag Civil no Discord continua bloqueado.",
        }),
        new("v1.46", "18 jul 2026", "Limpeza do PC",
        new()
        {
            "Novo cartão \"🧹 Limpeza do PC\" no Otimizador: um clique apaga ficheiros temporários (teus e do Windows), Prefetch, relatórios de erros antigos e esvazia a Reciclagem.",
            "É seguro: ficheiros em uso são ignorados e os teus documentos e jogos nunca são tocados. No fim diz quanto espaço libertou.",
        }),
        new("v1.45", "18 jul 2026", "Leve como uma pena",
        new()
        {
            "Quando a app está na bandeja (ao pé do relógio), agora desliga tudo: sensores, gráficos e medições param — gasto de processador praticamente zero.",
            "Na bandeja liberta também a memória que não precisa e baixa a prioridade, para nunca roubar desempenho aos teus jogos.",
            "O gráfico do Dashboard passou a gastar metade da placa gráfica, sem se notar diferença.",
        }),
        new("v1.44", "18 jul 2026", "Engrenagem no topo",
        new()
        {
            "Novo botão ⚙ na barra do topo — clica nele a qualquer momento para veres o que mudou nas últimas atualizações.",
            "Depois de cada atualização, a app abre esta página sozinha uma vez, para ficares sempre a par sem procurar nada.",
        }),
        new("v1.43", "18 jul 2026", "Aba Novidades",
        new()
        {
            "Esta página nova — a partir de agora podes ver aqui o que mudou em cada atualização.",
            "A app atualiza-se sozinha quando abre, por isso tens sempre a versão mais recente.",
        }),
        new("v1.42", "18 jul 2026", "Otimizador renovado",
        new()
        {
            "Os tweaks agora ligam-se com interruptores modernos em vez de caixas de visto.",
            "Botão do boost com novo visual: brilha, anima ao passar o rato e fica verde quando está ativo.",
            "Corrigido o tweak \"Interrupt moderation\" que falhava sem explicação em algumas placas de rede — agora funciona em muito mais PCs e, se não der, diz porquê.",
        }),
        new("v1.41", "18 jul 2026", "O meu PC + prioridade de rede",
        new()
        {
            "Nova página \"O meu PC\": vê todas as specs do teu computador (processador, gráfica, RAM, discos…) e copia tudo com um clique para pedir ajuda no Discord.",
            "Novo tweak em teste (🧪 BETA) no Otimizador: dá prioridade aos pacotes do WestRP no teu router — útil quando partilhas a net com mais gente.",
        }),
        new("v1.40", "18 jul 2026", "Respeito pelas tuas otimizações",
        new()
        {
            "Se já tens um plano de energia otimizado (Ultimate, Bitsum, etc.), o boost deixa de o substituir — mantém o teu e só melhora o Wi-Fi.",
        }),
        new("v1.39", "18 jul 2026", "Definições mais bonitas",
        new()
        {
            "A opção \"Iniciar com o Windows\" no Dashboard ganhou um interruptor novo estilo iPhone.",
        }),
        new("v1.38", "18 jul 2026", "Tu controlas o arranque",
        new()
        {
            "Iniciar com o Windows passou a ser opcional — desliga no Dashboard ou no menu da bandeja se preferires abrir a app à mão.",
        }),
    };

    public ChangelogView()
    {
        InitializeComponent();

        var list = Releases
            .Select((r, i) => r with { CurrentVis = i == 0 ? Visibility.Visible : Visibility.Collapsed })
            .ToList();
        ReleaseList.ItemsSource = list;
    }

}
