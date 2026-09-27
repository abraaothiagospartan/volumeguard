# VolumeGuard

Para quem usa fone e se incomoda com app e site abrindo com o volume "no talo". O VolumeGuard deixa **todo app abrir no volume que você escolher (em dB)**, barra propagandas estouradas e mostra **quantos dB você está ouvindo**, com histórico para cuidar da audição.

- **Volume inicial por app:** navegador, Discord, jogos e sons do sistema já abrem no volume escolhido, no instante em que começam a tocar.
- **Lembra o que você ajustar:** mexeu no volume de um app (aqui ou no mixer do Windows)? Ele passa a abrir daquele jeito.
- **Nível alvo (auto):** em vez de um volume fixo, escolha quantos dB quer ouvir (ex.: 68 dB). Cada app é ajustado sozinho para chegar perto disso.
- **Limitador:** se algo passar do limite (85 dB por padrão), o app responsável é abaixado em ~0,1 s.
- **dB no ouvido + histórico:** medição com ponderação A (como num decibelímetro), histórico por minuto, dose diária (NIOSH: 85 dB por 8 h) e semanal (OMS: 80 dB por 40 h).
- **Alertas na tela:** quando o som fica num nível alarmante e quando a dose do dia chega a 50%, 80% e 100%.
- **Volume do Windows em dB,** com ajuste fino abaixo do "1%".

## Instalar

1. Baixe o `VolumeGuard-Setup-<versão>.exe` em **[Releases](https://github.com/abraaothiagospartan/volumeguard/releases/latest)**.
2. Abra o arquivo. A instalação é só para o seu usuário e **não pede administrador**.
3. Na primeira vez, vá em **Ajustes → Seu fone** e escolha o tipo do seu fone (ou use a calculadora com a ficha técnica). É isso que faz os dB ficarem certos.

> **Aviso "O Windows protegeu o computador":** o instalador ainda não tem assinatura digital (um certificado pago). Por isso o SmartScreen pode avisar em apps novos. Clique em **Mais informações → Executar assim mesmo**. Se quiser conferir que o arquivo é o original, compare o SHA-256 com o do `SHA256SUMS.txt` da release:
> ```bash
> certutil -hashfile VolumeGuard-Setup-<versão>.exe SHA256
> ```

Prefere não instalar? Use o `VolumeGuard-<versão>-portatil.zip`: extraia e rode o `VolumeGuard.exe`.

**Requisitos:** Windows 10 ou 11 (64 bits). Usa o .NET Framework 4.8, que já vem no Windows.

**Desinstalar:** Configurações do Windows → Aplicativos → VolumeGuard → Desinstalar. O desinstalador pergunta se você quer apagar também o histórico e as configurações.

## Privacidade e segurança

- **Não acessa a internet.** Não há telemetria, atualização automática nem conta.
- **O som não é gravado.** O áudio que sai para o fone é analisado na memória só para calcular o nível em dB. O que fica salvo é o nível médio por minuto e o nome do app que estava tocando.
- **Tudo fica no seu PC,** em `%APPDATA%\VolumeGuard`:
  - `settings.json`: configurações, perfis de fone e volumes por app.
  - `historico\AAAA-MM-DD.csv`: uma linha por minuto com som.
  - `log.txt`: erros.
- **Sem administrador:** o app e o instalador rodam com as permissões do seu usuário. O instalador só grava em `%LOCALAPPDATA%\Programs\VolumeGuard`, no atalho do Menu Iniciar (e da área de trabalho, se você pedir) e nas chaves do seu usuário no registro (`HKCU`): a entrada de desinstalação e, se você escolher, a de iniciar com o Windows.
- **Sem dependências de terceiros:** o código usa só o .NET Framework e as APIs de áudio do próprio Windows.

### Os dB são uma estimativa

`dB no ouvido ≈ nível digital (dBFS, ponderação A) + volume do Windows + máximo do fone`.
O "máximo do fone" é a calibração em Ajustes. Com a ficha técnica, a estimativa fica próxima do real. Mesmo assim, não substitui um medidor calibrado nem orientação médica.

## Consumo

Na bandeja, sem som tocando: ~0,01% do processador e ~3 MB de memória. Tocando som, o motor mede 50 vezes por segundo (é o que permite cortar uma propaganda em 0,1 s). A janela só existe enquanto está aberta: ao fechar, a memória dela é devolvida ao Windows.

## Compilar

Não precisa instalar nada, porque o script usa o compilador do .NET Framework 4.8 que vem no Windows:

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Gera:

- `dist\VolumeGuard.exe`: o app.
- `release\VolumeGuard-Setup-<versão>.exe`: o instalador, que leva o app dentro.
- `release\VolumeGuard-<versão>-portatil.zip`.
- `release\SHA256SUMS.txt`.

Feche o app antes de recompilar, porque o exe em uso fica bloqueado. A versão fica em `src/Properties/BuildInfo.cs`.

O instalador também aceita parâmetros:

- Instalação silenciosa: `VolumeGuard-Setup.exe /silent [/desktop] [/noautostart] [/nolaunch]`.
- Desinstalação silenciosa: `uninstall.exe /uninstall /silent [/removedata]`.

Para diagnóstico, use `VOLUMEGUARD_DEBUG=1`, que grava o estado do motor a cada segundo no log. Com `VOLUMEGUARD_DATA=<pasta>` o app usa outra pasta de dados, útil para testes.

## Estrutura

| Pasta/arquivo | O quê |
|---|---|
| `src/Audio/CoreAudio.cs` | Interop com a Core Audio API do Windows (sessões, volume, medidores, WASAPI) |
| `src/Audio/AudioEngine.cs` | Motor: sessões por app, volume inicial, nível alvo, limitador, alertas |
| `src/Audio/LoopbackMeter.cs` | Captura loopback + filtro de ponderação A |
| `src/Core/Exposure.cs` | Dose (NIOSH/OMS), histórico em CSV |
| `src/Core/Settings.cs` | Configurações e perfis de fone |
| `src/UI/*` | Interface WPF (telas em `src/UI/Xaml`), gráficos, bandeja e alerta |
| `installer/*` | Instalador/desinstalador por usuário |

## Licença

[MIT](LICENSE): pode usar, modificar e redistribuir à vontade, mantendo o aviso de copyright.
