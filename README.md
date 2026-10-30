# Doca

Dock e barra de menu estilo macOS para Windows 11, em .NET 10 / WPF.

## Instalar / atualizar

```powershell
.\install.ps1             # publica em %LOCALAPPDATA%\Programs\Doca, cria atalho no Menu Iniciar e inicia
.\install.ps1 -Startup    # idem + iniciar com o Windows
.\install.ps1 -Uninstall  # remove (preferências ficam em %APPDATA%\Doca)
```

A cópia instalada é separada da pasta do código: dá para compilar e testar sem fechar o Doca em uso.

## Desenvolver

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\Doca.exe --preview   # não mexe na taskbar
```

Linha de comando: `--quit` fecha a instância aberta (restaurando tudo), `--settings` abre as Preferências, `--preview` testa sem esconder a taskbar.

Antes de publicar uma mudança grande, passe pela [lista de testes](TESTES.md).

## Recursos

- **Dock** (uma por tela): ampliação, esconder automaticamente, badges de notificação, pré-visualização de janelas, arrastar para reordenar/fixar/remover, lixeira.
- **Barra de menu** (uma por tela): menus do app em foco, ícones da bandeja (inclusive os escondidos), painéis próprios de calendário, som (volume e saída), Wi-Fi e Bluetooth.
- Some só na tela que tem um app em tela cheia; cantos arredondados; Alt+` alterna janelas do mesmo app.
- Tudo configurável em **Preferências** (logo da barra → Preferências do Doca…).

## Recuperação automática

O Doca inicia um processo vigia (`Doca.exe --watchdog <pid>`, sem interface). Se o Doca terminar sem passar pela saída normal:

| Como terminou | O vigia |
|---|---|
| Travamento | libera o espaço das barras, restaura a taskbar e reabre o Doca (até 3 vezes em 2 minutos) |
| "Finalizar tarefa" no Gerenciador | libera o espaço e restaura a taskbar, sem reabrir |

Ocorrências ficam em `%APPDATA%\Doca\log.txt`.

## Arquitetura

| Arquivo | Responsabilidade |
|---|---|
| `DockWindow.cs` | Dock: layout, animação (vsync, só quando algo se move), entrada, arrastar |
| `MenuBarWindow.cs` | Barra de menu e itens de status |
| `Flyouts.cs` | Painéis de calendário, som, Wi-Fi e Bluetooth |
| `AppMenus.cs` | Menus do app (Win32, UI Automation ou menus padrão) |
| `WindowTracker.cs` | Janelas abertas e tela cheia por monitor (WinEvent hooks) |
| `Tray.cs` | Bandeja do sistema (ManagedShell) e badges |
| `Preview.cs`, `Corners.cs`, `Hotkeys.cs` | Miniaturas DWM, cantos arredondados, Alt+` |
| `Audio.cs`, `Wlan.cs`, `Bluetooth.cs` | Core Audio, Wi-Fi nativo, rádios e dispositivos Bluetooth |
| `AppBar.cs`, `Watchdog.cs`, `Program.cs` | Reserva de espaço, taskbar, recuperação |
