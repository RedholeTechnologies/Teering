# 인계

다음 사람이 처음 30초에 알아야 할 것. 규칙은 [CLAUDE.md](CLAUDE.md), 설계는 [treering-plan.md](treering-plan.md) 에 있다.

## 지금 상태 — 2026-09-29, 직접 잰 값

- `develop` `23c19f5` (#35), `main` `273ae20` (#36) = **v0.7.1**. 열린 PR 없음, 작업 브랜치 없음.
- v0.7.1 릴리스 워크플로: 5개 작업 모두 성공. Windows zip 47 MB, Linux tar.gz 46 MB, `.mcpb` 2개, `SHA256SUMS`. MCP Registry 에 `io.github.RedholeTechnologies/treering` 0.7.1.
- `dotnet test Treering.slnx` → Core 319 · Cli 18 통과. CI 는 **태그 push 때만** 돈다(`release.yml`, Windows). PR 에는 CI 가 없으니 머지 전에 로컬에서 돌린다.
- 담당자 PC 의 로컬 서버 127.0.0.1:7377 은 v0.7.1 과 같은 코드(develop 빌드). 로그온 자동 시작은 꺼 두었다.

## 해야 할 것

- **eShop 분석 글 게시.** 초안 `post.md` 와 PNG 4장은 2026-09-28 대화에 보낸 파일 카드로만 남아 있다. 로컬 사본은 `%TEMP%` 가 정리되면서 사라졌다. 어디에 올릴지는 아직 정하지 않았다. 글에 쓴 사실은 전부 eShop 소스와 커밋으로 확인했다.
- **영어판 초안**(Reddit · SCIP 커뮤니티). 게시는 담당자가 한다.
- **GitHub social preview 이미지 업로드.** 설정 화면에서 담당자가 직접 해야 한다. 만들어 둔 이미지도 스크래치패드와 함께 사라졌다. 그 전에 대화로 보낸 카드가 있다.
- **설계도의 바깥 상자는 무거운 순으로 8개까지만 그린다.** 그래서 가는 의존이 그림에 안 나온다. eShop 에서 Domain → MediatR 이 안 보여 글에 틀린 결론을 쓸 뻔했다. 한도를 보이게 할지 정해야 한다.
- **큰 장(30상자 규모)에서 도면이 화면에 다 안 들어온다.** 맞춤 확대의 하한이 0.4 이고, 연결이 없는 상자가 멀리 따로 놓인다.
- **빌드가 `obj/` 아래 만든 생성 코드가 색인에 들어온다.** 모듈의 역할 문구를 거기서 빌려 오고, 변화 목록도 흐린다. 걸러낼지 정해야 한다.
- **`args` 를 한 번도 쓰지 않는 최상위 문의 호출은 여전히 빠진다**(#32 의 한계).
- NativeAOT, TS · Python 시험대, 「20만 심볼에서 300ms」 는 이미 기획서 마일스톤 절에 있다.

## 테스트한 것 — 결함을 넣어 실패를 확인했다

- 로컬 서버의 관문(JSON + Origin). 다른 사이트, 다른 포트, `null` 출처, 미리 묻지 않는 폼 3종이 모두 403 이다. sample · import · watch 가 각각 이 관문을 지난다(#34, `ServerGateTests`).
- MCP 가 어느 프로젝트에 답하는지 가리는 규칙(#34, `McpProjectChoiceTests`).
- 최상위 문의 호출을 `Program` 에 붙이는 것, 겹침 표시 `(+1)` 이 붙어도 같은 `Program` 인 것(#32). `*Tests` 로 끝나는 프로젝트를 테스트로 보는 규칙(#33).
- 예외: `The_generated_program_counts_as_our_own_code` 는 수정 전에도 통과한다. 아무것도 증명하지 않는다.

## 테스트해야 할 것

- `/api/pick-folder` 의 관문. 깨뜨리면 실제 폴더 창이 떠서 개별 테스트에서 뺐다. 같은 관문 함수를 쓴다는 것에만 기대고 있다.
- 나머지 HTTP API(`/api/blueprint` 의 매개변수 해석 등). 테스트가 없다.
- `Import.Run`, `Incremental.Index`. 실제 색인기와 git 을 돌려서 단위 테스트가 없다.
- 화면(`index.html`). 자동 테스트가 없다. 테스트가 초록이라도 화면이 맞다는 뜻은 아니니 눈으로 본다.

## 함정 — 이번 세션에서 실제로 밟은 것

- **배포용 exe 는 `release.yml` 과 같은 옵션으로 만든다**(self-contained 단일 파일, 약 50 MB). 그냥 `dotnet publish` 하면 162 KB 짜리가 나오고, 설치하면 서버가 말없이 안 뜬다.
- **`%TEMP%` 와 세션 스크래치패드는 하루 사이에 지워진다.** 분석 DB, 초안, 배포 스크립트를 거기 두지 않는다.
- **Claude 세션이 쓰는 `%LOCALAPPDATA%` 는 앱 전용 캐시로 우회된다.** 실제 설치본을 바꾸려면 1회용 작업 스케줄러로 한다.
- **옛 .NET 저장소를 색인할 때:** `global.json` 을 통째로 지우면 `msbuild-sdks`(`MSTest.Sdk`)까지 사라진다. `NuGet.config` 를 grep 으로 줄 단위로 지우면 XML 이 깨진다. 복원은 솔루션이 아니라 프로젝트마다 한다(MAUI 가 있으면 거기서 멈춘다).
- **설계도 그림만 보고 결론 내지 않는다.** 위의 바깥 상자 한도 때문이다. 숫자는 DB 나 MCP 에 묻는다.
