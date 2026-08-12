# ScrapMap

Protótipo de mapa interativo para mundos Survival do Scrap Mechanic 1.0.

O aplicativo localiza os saves em `%APPDATA%\Axolot Games\Scrap Mechanic`, abre o SQLite em modo somente leitura e usa os próprios scripts instalados do jogo para identificar recursos e peças.

## Executar

```powershell
dotnet run --project .\src\ScrapMap.Desktop\ScrapMap.Desktop.csproj -c Release
```

Também é possível abrir diretamente `src\ScrapMap.Desktop\bin\Release\net8.0-windows\ScrapMap.Desktop.exe` depois da compilação.

## O que já funciona

- detecção automática dos saves Survival;
- leitura segura do `.db`, sem gravar no mundo original;
- mapa navegável com zoom, arraste e coordenadas do cursor;
- terreno real renderizado em vista superior a partir da altura e dos materiais dos tiles do jogo;
- escolha entre a aproximação da área visitada, o mundo completo e a visualização sem terreno;
- área-base limitada às células carregadas/persistidas pelo jogo, como aproximação da região explorada;
- camadas independentes para árvores, pedras, milho, flores, mariscos, petróleo, colmeias e suprimentos;
- todas as camadas de marcadores começam desligadas;
- leitura por snapshot temporário estável, sem abrir o SQLite original do jogo;
- atualização automática experimental, desligada por padrão, preservando zoom e camadas escolhidas;
- catálogo automático de UUIDs a partir dos Lua, `.harvestableset`, `.shapeset` e JSON instalados com o jogo;
- localização de construções, peças soltas, nave inicial e veículos;
- agrupamento de rodas/suspensões/corpos ligados por joints em uma única criação;
- popups com posição, quantidade de peças e componentes identificados.

O terreno incluído atualmente corresponde ao seed `599604130` e cobre o mundo completo de 144×112 células. O jogo não armazena uma trilha GPS do jogador; por isso, **Área registrada no save** usa as células persistidas como aproximação do que já foi visitado. A atualização dos recursos e construções continua sendo feita pelo botão **Atualizar mapa** usando um snapshot seguro do save.

O raster de altura, materiais e água foi gerado a partir dos arquivos instalados do jogo com o renderizador MIT [parrotlive/ScrapMap](https://github.com/parrotlive/ScrapMap). Os filtros, fog-of-war, leitura de recursos e interface permanecem implementados neste projeto.

## Estrutura

- `src/ScrapMap.Core`: localização e parser dos saves;
- `src/ScrapMap.Desktop`: aplicativo WPF + WebView2 + Leaflet;
- `tools/SaveInspector`: relatório detalhado do schema/BLOBs;
- `tools/ScrapMap.SmokeTest`: validação do parser contra um save local.
- `tools/TileMapProof`: exportação e composição visual do layout de tiles.

## Validar o ambiente

```powershell
dotnet run --project .\tools\ScrapMap.SmokeTest\ScrapMap.SmokeTest.csproj
```

Os arquivos `.db` dentro de `samples` estão ignorados pelo Git para evitar publicar saves pessoais por acidente.
