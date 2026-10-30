# Doca

Dock e barra de menu estilo macOS para Windows 11, em um único processo .NET 10 / WPF.

## Rodar

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\Doca.exe              # modo normal (esconde a taskbar)
.\bin\Release\net10.0-windows\Doca.exe --preview    # testa sem mexer na taskbar
.\bin\Release\net10.0-windows\Doca.exe --quit       # fecha a instância aberta e restaura tudo
```

Ou abra `Doca.csproj` no Visual Studio e pressione F5.

Executável único para copiar para outra pasta:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Configuração

`%APPDATA%\Doca\config.json`. Na primeira execução, os apps fixados e os tamanhos dos ícones são importados do MyDockFinder.
Use "Preferências do Doca…" no menu do logo e depois "Reiniciar o Doca".

## Dock

- Clique: abre o app, traz para frente, minimiza (se já estiver em foco) ou alterna entre as janelas
- Shift+clique ou clique do meio: nova instância
- Botão direito: lista de janelas, Manter na Dock / Remover, Mostrar no Explorador, Encerrar
- Arrastar arquivo, atalho ou .exe para a Dock: fixa o item
- Botão direito no fundo da Dock: ampliação e ocultar automaticamente

## Barra de menu

- Logo: menu do sistema (Sobre, Configurações, Suspender, Reiniciar, Desligar, Bloquear, iniciar com o Windows, sair)
- Nome do app em foco: ocultar o app, ocultar os outros, minimizar, maximizar ou fechar a janela, encerrar o app
- Volume: role a roda do mouse para ajustar, botão direito para silenciar
- Wi-Fi, volume e bateria abrem as Configurações rápidas (Win+A); o relógio abre a Central de notificações (Win+N)

## Por que é mais fluido que o MyDockFinder

| | MyDockFinder | Doca |
|---|---|---|
| Composição | `UpdateLayeredWindow`: a CPU monta um bitmap e copia para o DWM a cada frame | Superfície DWM direto na GPU (`DwmExtendFrameIntoClientArea`) |
| Ritmo da animação | `SetTimer` (resolução de ~15,6 ms, sem vsync) | `CompositionTarget.Rendering`, sincronizado com o vsync, com suavização independente do frame rate |
| Em repouso | timers ativos | loop desligado e atualização por eventos (WinEvent hooks): CPU ≈ 0 |
| Ícones | — | carregados numa thread STA separada e pré-escalados, sem travar a UI |
| Barra superior | — | registrada como AppBar (janelas maximizadas param nela), some em apps de tela cheia e se registra de novo se o Explorer reiniciar |

Se o Doca for fechado à força e a taskbar ficar escondida, rode o Doca de novo e use "Sair do Doca", ou reinicie o Explorer.
