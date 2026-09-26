# <img src="docs/logo.svg" width="40" height="40" alt="" align="top"> Treering

[English](README.md) | 한국어

Treering 은 코드베이스의 구조를 지도로 그리고, 커밋마다 그 지도를 남겨 둡니다. 그래서 지난주 이후 어떤 타입들이 새로 엮였는지, 어떤 클래스를 부르는 곳이 언제부터 늘었는지를 볼 수 있습니다. 모든 작업은 내 컴퓨터에서 이루어지고 네트워크로 나가는 데이터는 없습니다. LLM 을 쓰지 않기 때문에 같은 코드에서는 언제나 같은 그래프가 나옵니다.

아직 개발 중이라 릴리스가 없고, DB 형식도 바뀔 수 있습니다. 설계에 대한 기록은 [treering-plan.md](treering-plan.md) 에 있습니다.

## 색인기 설치

Treering 은 소스 코드를 직접 해석하지 않고, 언어별 색인기가 만든 [SCIP](https://github.com/sourcegraph/scip) 색인을 읽습니다. 쓰는 언어에 맞는 색인기를 설치하세요.

```bash
dotnet tool install --global scip-dotnet       # C#
npm install -g @sourcegraph/scip-typescript    # TypeScript, JavaScript
npm install -g @sourcegraph/scip-python        # Python
```

## 저장소 가져오기

```bash
treering import /path/to/repo
treering serve
```

`import` 는 저장소에 어떤 언어가 있는지 살펴보고, 언어마다 필요한 옵션을 붙여 색인기를 실행합니다. C# 은 루트의 솔루션을 색인하고, 솔루션이 없으면 프로젝트마다 색인합니다. TypeScript 는 가장 위에 있는 `package.json` 마다, Python 은 `pyproject.toml` 마다 색인합니다. 그래프는 사용자 폴더에 저장되고 저장소 안에는 아무것도 쓰지 않습니다. 가져온 목록은 `treering projects` 로 볼 수 있습니다.

`treering serve` 를 실행하면 http://127.0.0.1:7377 에서 지도가 열립니다. DB 를 지정하지 않으면 가져온 프로젝트를 모두 보여 주고, 화면 위쪽 메뉴에서 프로젝트를 바꿀 수 있습니다.

화면에서 바로 가져올 수도 있습니다. 프로젝트 메뉴에서 **가져오기…** 를 고른 다음 **폴더 고르기…** 를 누르세요. 서버가 운영체제의 폴더 선택 창을 띄웁니다(Windows 는 탐색기, macOS 는 기본 선택 창, Linux 는 zenity 나 kdialog). 브라우저는 고른 폴더의 전체 경로를 페이지에 알려 주지 않기 때문에 서버가 창을 띄우는 방식을 씁니다. 경로를 직접 입력해도 됩니다. 폴더를 고르면 바로 가져오기가 시작되고, 색인기마다 진행 상황이 한 줄씩 표시됩니다. 실패하면 이유와 해결 방법을 알려 줍니다.

가져오기와 폴더 선택 요청은 이 화면에서 보낸 JSON 만 받습니다. 그래서 브라우저에 열린 다른 사이트가 가져오기를 시작하거나 창을 띄울 수 없습니다.

## 지도 보는 법

지도에서 모듈, 네임스페이스, 타입은 점으로, 그 사이의 참조는 선으로 나타납니다.

- 점을 누르면 그 안으로 들어갑니다. 들어간 대상은 내용물을 둘러싼 고리로 화면에 남습니다. 빈 곳을 누르거나, 고리를 오른쪽 버튼으로 누르거나, Esc 를 누르면 한 단계 위로 나오고, 방금 나온 대상에 표시가 붙습니다.
- 색은 누구의 코드인지를 나타냅니다. 내 코드는 모듈마다 다른 색이고 라이브러리는 회색입니다. 크기는 얼마나 많이 쓰이는지를 나타냅니다.
- **모양** 메뉴에서 같은 그래프를 다섯 가지로 배치할 수 있습니다. 성운, 궤도, 고리, 층계, 계층도입니다. 층계는 의존 관계를 위에서 아래로 쌓고 순환하는 것끼리는 한데 묶습니다. 계층도는 무엇이 무엇 안에 있는지를 가지 하나씩 펼쳐 보여 줍니다.
- 옆 패널에는 지금 들어가 있는 대상이나 누른 타입의 정보가 나옵니다. 멤버, 주입받는 것과 주입되는 곳, 구현하는 인터페이스, 부르는 곳과 부르는 대상이 나오고, 이름을 누르면 그곳으로 이동합니다.
- `/` 나 `Ctrl+K` 로 검색할 수 있습니다. 결과를 고르면 그 대상이 있는 곳으로 이동합니다.
- **시점** 과 **비교** 로 특정 스냅샷의 그래프를 보거나, 두 스냅샷 사이에 생기고 없어진 선을 볼 수 있습니다.
- **범위** 에서 전부 또는 **우리 코드** 만 볼 수 있습니다. 색인 안에 정의가 있는 심볼만 내 코드로 치기 때문에, 이름으로 짐작하지 않고도 라이브러리가 빠집니다. Acme.Shop 에서는 모듈이 38개에서 4개로, 타입이 829개에서 401개로 줄었습니다.
- **테마** 는 밝게, 어둡게, 슬레이트, 스카이폴, 옵시디언 중에서 고를 수 있고, **언어** 는 한국어와 영어를 지원합니다.
- 운영체제에서 동작 줄이기를 켜 두면 꾸밈용 애니메이션이 멈춥니다.

## 최신으로 유지하기

```bash
treering update /path/to/repo
treering watch /path/to/repo       # 10분마다
treering watch /path/to/repo 30    # 30분마다
```

`update` 는 파일이 바뀐 프로젝트만 다시 색인합니다. 바뀐 파일마다 가장 가까운 프로젝트(`.csproj`, `package.json`, `pyproject.toml`, `setup.py`)를 찾아 그 언어의 색인기로 처리하므로, 여러 언어가 섞인 저장소도 부분마다 맞는 색인기로 갱신됩니다. 색인기가 설치돼 있지 않으면 설치 명령을 알려 줍니다.

`watch` 는 정해진 간격으로 `update` 를 실행하고, 실제로 바뀐 것이 있을 때만 스냅샷을 추가합니다. 작업하는 동안 기록이 저절로 쌓입니다. 마지막 스냅샷에 기록된 커밋에서 이어서 시작하므로 껐다 켜도 빠지는 것이 없고, 커밋하지 않은 변경도 함께 반영합니다.

보통은 `watch` 를 따로 띄울 필요가 없습니다. `treering serve` 가 떠 있는 동안 가져온 프로젝트 전부에 같은 일을 합니다. 10분마다 바뀐 것을 다시 색인해 스냅샷을 추가하고, 최신 스냅샷을 보고 있었다면 화면이 새 스냅샷으로 저절로 넘어갑니다. 다시 색인했는데 그래프가 그대로면 스냅샷을 남기지 않습니다. 끄려면 `--no-watch` 를 붙이거나 화면의 **설정** 에서 끄고, 주기는 `--watch-minutes N`, 포트는 `--port N` 으로 바꿉니다.

## 색인을 직접 다루기

색인기를 직접 실행하고 그 결과를 불러올 수도 있습니다. C# 의 경우:

```bash
scip-dotnet index YourSolution.slnx --allow-global-symbol-definitions --output index.scip
treering load index.scip graph.db
treering serve graph.db
treering callers graph.db OrderRepository
treering map graph.db type --own
```

scip-dotnet 에는 `--allow-global-symbol-definitions` 를 꼭 붙이세요. 이 옵션이 없으면 심볼에 패키지 이름이 붙지 않아서, 프로젝트를 따로 색인하거나 스냅샷을 쌓을 때 같은 심볼이 여러 개로 갈라집니다. 실제로 해 보니 심볼 6,784개가 새로 생기고 6,790개가 사라진 것으로 기록됐습니다.

시간에 따른 변화를 보려면 스냅샷을 더 쌓습니다.

```bash
treering snap index-new.scip graph.db 1
treering diff graph.db 0 1
treering growth graph.db InvoiceModel
```

`diff` 는 두 스냅샷 사이에 생기고 없어진 의존 관계를 보여 주고, `growth` 는 어떤 타입을 부르는 곳이 언제 늘었는지 보여 줍니다.

## 에이전트에서 쓰기

```bash
treering mcp graph.db
```

stdio 로 MCP(Model Context Protocol) 서버를 띄웁니다. 도구는 `find_symbol`, `callers_of`, `subgraph`, `changed_since`, `snapshots` 다섯 가지입니다. 화면과 같은 질의를 같은 크기 제한으로 실행하고, 결과가 너무 크면 무엇이 빠졌는지 알려 주어 에이전트가 범위를 좁혀 다시 물을 수 있게 합니다.

## 성능

Acme.Shop (프로젝트 4개, `.cs` 파일 632개) 기준입니다.

| 항목 | 결과 |
|---|---|
| 색인 (scip-dotnet) | 89초 |
| 그래프 적재 | 2.6초, 최대 메모리 53 MB |
| DB 크기 | 5.9 MB (`index.scip` 는 8.2 MB) |
| 스냅샷 하나 추가 시 | 27% 증가 (5.9 MB에서 7.5 MB) |
| 전체 지도 질의 | 모듈 29 ms, 네임스페이스 30 ms, 타입 전체 48 ms |
| 세부 질의 | 182 ms (타입 384개, 선 2,933개) |
| 증분 갱신 | 17초, 그중 16.6초가 색인 |

큰 C# 저장소(`.cs` 파일 11,385개) 기준입니다.

| 항목 | 결과 |
|---|---|
| 색인 | 3분 28초, 색인 파일 34.5 MB |
| 심볼, 선 | 43,932개, 185,266개 |
| 스캔 메모리 | 41.6 MB (8.2 MB 색인과 같음) |
| 적재 | 11~15초, 103 MB, DB 40.3 MB |
| 전체 지도 질의 | 모듈 115개 26 ms, 네임스페이스 406개 48 ms, 타입 전체 210 ms (2,000개로 제한) |

스캔 메모리는 색인 크기와 상관없이 일정하고, 이 규모에서도 전체 지도는 모두 300 ms 안에 나옵니다. 모듈, 네임스페이스, 타입 단위의 선을 질의할 때마다 계산하지 않고 적재할 때 한 번만 미리 합쳐 두기 때문입니다. 이렇게 바꾼 뒤 모듈 지도가 327 ms 에서 26 ms 로 줄었습니다.

## 지원 언어

C#, TypeScript, Python 을 지원합니다. 입력이 SCIP 이기 때문에 언어를 추가할 때는 대부분 색인기만 붙이면 됩니다. TypeScript 는 Treering 코드를 전혀 고치지 않고 동작했고, 모듈은 npm 패키지로, 네임스페이스는 폴더로 자연스럽게 바뀌었습니다. 생성자 주입은 `.ctor`, `<constructor>`, `__init__`, `<init>` 을 모두 인식합니다. `update` 와 `watch` 도 TypeScript 를 처리하며, ShopWeb 에서 패키지 하나의 파일 19개가 바뀌었을 때 52초가 걸렸습니다.

scip-python 0.6.6 은 Windows 에서 그대로는 실행되지 않습니다. 경로 구분자로 정규식을 만드는데, `\` 하나만으로는 올바른 패턴이 아니기 때문입니다. Treering 은 scip-python 보다 먼저 작은 스크립트를 불러와 그 패턴 하나만 바로잡습니다. scip-python 이 쓸 pip 도 찾아 주는데, 저장소의 `.venv` 를 먼저 보고 없으면 `py` 런처가 아는 Python 을 찾습니다. pip 이 전혀 없으면 빈 패키지 목록으로 색인하며, 이때 잃는 것은 외부 패키지 이름뿐입니다. python-sample 에서는 51.6초 동안 심볼 1,800개, 선 6,564개가 나왔습니다.

언어마다 특성은 조금씩 다릅니다. TypeScript 는 구조적 타입 시스템 때문에 색인기가 `implements` 를 거의 내보내지 않아서 상속 관계가 적게 나옵니다. 또 TypeScript 참조의 14.8% 는 어느 정의에 속하는지 정할 수 없는데(C# 은 1.4%), 파일 맨 위의 import 와 모듈 수준 코드가 정의보다 앞에 오기 때문입니다.

## 알려진 한계

SCIP 는 참조가 어디에 있는지는 알려 주지만, 그 참조가 어느 심볼 안에 있는지는 알려 주지 않습니다(`enclosing_symbol` 이 비어 있습니다). 그래서 Treering 은 각 참조를 바로 앞에 있는 가장 가까운 정의에 붙입니다. 타입 단위에서는 한 파일의 참조가 그 파일이 정의하는 타입에 속하므로 정확합니다. 메서드 단위에서는 중첩 타입, 람다, 로컬 함수, 프로퍼티 접근자, 필드 초기화 식에서 틀릴 수 있어서, 화면과 MCP 도구에서 이런 선은 추정으로 표시합니다.

증분 갱신은 10초 아래로 줄이기 어렵고, 그 시간은 거의 전부 색인기가 씁니다. 가장 작은 C# 프로젝트도 scip-dotnet 에서 16.6초가 걸리고, scip-typescript 는 ShopWeb 패키지 하나에 50초가 걸립니다. Treering 자체가 비교하고 적재하는 데 드는 시간은 0.5초가 안 됩니다.

## 지원하지 않는 것

다음은 앞으로도 넣지 않을 계획입니다.

- 자체 언어 파서 (SCIP 색인기가 맡습니다)
- LLM, 임베딩, 벡터 검색 (같은 코드에서 같은 그래프가 나와야 합니다)
- 클라우드 동기화, 계정, 사용 통계 수집
- 코드 편집

## 빌드

```bash
dotnet test
dotnet publish src/Treering.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

약 52 MB 짜리 실행 파일 하나가 만들어집니다. 웹 화면이 실행 파일 안에 들어 있어서 Node 를 포함해 따로 설치할 것이 없습니다. `IncludeNativeLibrariesForSelfExtract` 를 빼면 `e_sqlite3.dll` 이 실행 파일 옆에 따로 놓입니다.

NativeAOT 는 아직 동작하지 않습니다. JSON 직렬화가 리플렉션을 쓰고 MCP SDK 가 어셈블리를 스캔하기 때문입니다.

## 라이선스

[Apache-2.0](LICENSE). 배포용 실행 파일에 함께 들어가는 라이브러리와 그 라이선스는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 에 있습니다.
