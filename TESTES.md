# Lista de testes manuais

Marque `[x]` ao passar. Anote o que falhar ao lado.

## Dock
- [ ] Encostar o mouse na borda de baixo (meio e laterais) mostra a Dock; ela não pisca e some ao subir o mouse
- [ ] A Dock aparece nas duas telas
- [ ] Ampliação suave ao passar o mouse; rótulo com o nome do app
- [ ] Clique abre / traz para frente / minimiza; Shift+clique ou botão do meio abre nova janela
- [ ] Botão direito: lista de janelas (✓ na da frente, ◆ nas minimizadas), Nova janela, Ocultar/Mostrar, Encerrar
- [ ] Jump List no botão direito: Chrome (Nova janela anônima, Fechadas recentemente), Steam (jogos recentes, Loja), VS Code (pastas recentes), Explorador (Fixado/Frequentes); clicar abre o item; funciona também com o app fechado
- [ ] Opções ▸ Manter na Dock (marca/desmarca), Abrir ao Iniciar Sessão (Steam marcado, Discord não; alternar aparece igual no Gerenciador de Tarefas → Inicializar), Mostrar no Explorador
- [ ] Segurar Shift ou Alt com o menu aberto troca "Encerrar" por "Forçar Encerramento" (e volta ao soltar); app travado já mostra "Forçar"
- [ ] Arrastar ícone para os lados reordena; arrastar para cima remove; arrastar app aberto para a área fixa fixa
- [ ] Arrastar um .exe/.lnk/arquivo para a Dock fixa o item
- [ ] Pré-visualização: parar o mouse num app aberto mostra miniaturas; clique foca; "✕" fecha a janela
- [ ] Badge: mensagem não lida no Discord/WhatsApp aparece no ícone
- [ ] App pedindo atenção: bolinha vermelha, ícone pula e a Dock escondida aparece
- [ ] Lixeira: abre; "Esvaziar Lixeira" funciona

## Barra de menu
- [ ] Aparece nas duas telas; janelas maximizadas param abaixo dela
- [ ] Nome do app em foco atualiza ao trocar de app
- [ ] Botão direito em qualquer item (logo, nome do app, Arquivo…, Wi-Fi, Som, Central de Controle, relógio) abre o mesmo menu/painel do esquerdo; botão do meio no Som silencia
- [ ] Menus do app: WinRAR/Notepad++ (menu real), Bloco de Notas (menu do app), Chrome (menus padrão)
- [ ] Clicar em um item de menu executa o comando (ex.: Editar → Copiar/Colar no Chrome)
- [ ] Música: item aparece com Spotify/YouTube tocando, some sem player; painel tem capa, tempo andando, ⏮ ⏯ ⏭ funcionam; clicar na capa abre o player
- [ ] Central de Controle: Wi-Fi/Bluetooth ligam e desligam; modo escuro muda o Windows; brilho muda os dois monitores (nomes LG ULTRAGEAR e 2460G5); volume; clicar no nome da saída abre o painel de som
- [ ] Dock: download no Chrome mostra barra azul no ícone; pausado fica amarelo
- [ ] Menu aberto (logo, nome do app, Arquivo…, botão direito na Dock) fecha ao clicar em outro app ou na área de trabalho
- [ ] Ícones da bandeja: clique esquerdo/direito/duplo funcionam no app; arrastar reordena
- [ ] Calendário: abre embaixo do relógio, navega meses, Esc fecha
- [ ] Som: volume, mudo, trocar saída (fone ↔ caixa) muda o áudio de verdade
- [ ] Wi-Fi: lista redes, desconectar/conectar rede salva, rede nova com senha, liga/desliga Wi-Fi
- [ ] Bluetooth: liga/desliga, conectar/desconectar Galaxy Buds e Echo Dot, bateria aparece
- [ ] Logo: Suspender, Bloquear, Preferências, Reiniciar o Doca, Sair do Doca

## Tela cheia e sistema
- [ ] Vídeo do Chrome em tela cheia: barra e Dock somem só naquela tela
- [ ] Jogo em tela cheia: idem; voltam ao sair
- [ ] Vídeo em tela cheia: ponteiro no topo por ~¼ s mostra a barra por cima; some ao descer (não some com menu/painel aberto); o vídeo continua em tela cheia
- [ ] Jogo com o ponteiro preso (Valorant em partida): a barra não aparece no topo
- [ ] Chrome maximizado na segunda tela ao abrir um jogo: a barra continua lá e o Chrome não trava
- [ ] Alt+` alterna janelas do mesmo app; Alt+Shift+` volta
- [ ] Cantos arredondados nas duas telas
- [ ] Monitor com escala diferente (125%/150%): tamanhos e posições corretos
- [ ] Suspender e voltar: tudo continua funcionando
- [ ] Reiniciar o Explorer (Gerenciador de Tarefas): barra/Dock voltam a reservar espaço, taskbar continua escondida

## Recuperação
- [ ] "Sair do Doca": taskbar do Windows volta normal
- [ ] Encerrar o Doca pelo Gerenciador de Tarefas: taskbar volta; Doca **não** reabre
- [ ] Travamento (simulado): taskbar volta e o Doca reabre sozinho
