---
title: 명령줄 도구
description: holysheet 명령으로 터미널과 CI에서 검사하고 빌드하는 방법입니다.
sidebar:
  order: 7
---

`holysheet` 명령은 HolySheet 창이 하는 일을 터미널에서 합니다. 같은 `holysheet.json`을 읽고 같은 파일을 만들기 때문에, 개발자 컴퓨터에서 창으로 빌드한 결과와 CI에서 명령으로 빌드한 결과가 같습니다.

## 설치하기

.NET 10 SDK가 필요합니다. 저장소를 받은 뒤 .NET 도구로 설치합니다.

```bash
dotnet pack dotnet/HolySheet.Cli -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts HolySheet.Cli
holysheet help
```

설치하지 않고 바로 실행할 수도 있습니다.

```bash
dotnet build dotnet/HolySheet.Cli -c Release
dotnet dotnet/HolySheet.Cli/bin/Release/net10.0/holysheet.dll help
```

## 처음 써 보기

빈 폴더에서 다음 세 줄을 실행하면 샘플 시트로 전체 과정을 볼 수 있습니다.

```bash
holysheet init --sample     # holysheet.json과 샘플 시트(CSV)를 만듭니다
holysheet build             # 코드와 데이터를 만듭니다
holysheet watch             # 시트를 저장할 때마다 다시 빌드합니다
```

빌드가 끝나면 무엇을 썼는지 한 줄로 알려 줍니다.

```text wrap
built: 3 tables, 6 rows, 3 enums; 13 files written, 0 deleted, 0 unchanged (read 8 ms, build 22 ms, csv builtin)
```

## 명령

명령은 현재 폴더의 `holysheet.json`을 읽습니다. 다른 파일을 쓰려면 `-c 경로`를 붙입니다.

| 명령 | 하는 일 |
| --- | --- |
| `holysheet init` | `holysheet.json`을 만듭니다. `--sample`은 샘플 시트를, `--unity`는 Unity 프로젝트용 출력 폴더를 함께 설정합니다. |
| `holysheet check` | 시트를 읽고 검사만 합니다. 코드와 데이터는 쓰지 않습니다. |
| `holysheet build` | 시트를 읽고 코드와 데이터를 씁니다. |
| `holysheet watch` | 빌드한 뒤, 시트가 바뀔 때마다 다시 빌드합니다. |
| `holysheet diff <이전> <이후>` | 두 빌드 사이에 바뀐 테이블을 보여 줍니다. |
| `holysheet verify <데이터 폴더>` | 데이터 폴더의 해시와 서명을 확인합니다. |
| `holysheet keygen` | 서명용 키 쌍을 만듭니다. |
| `holysheet info` | 실행 환경과 쓸 수 있는 직렬화 라이브러리를 보여 줍니다. |

## 자주 쓰는 옵션

| 옵션 | 쓰는 명령 | 의미 |
| --- | --- | --- |
| `--offline` | build, check, watch | 스냅샷 폴더가 있는 Google 소스를 내려받지 않고 스냅샷으로 빌드합니다. |
| `--only <이름>` | build, check | 지정한 워크북이나 시트만 다시 읽습니다. [선택한 시트만 빌드하기](../partial-build/#명령줄에서) |
| `--label <글자>` | build, watch | 매니페스트에 버전 같은 표시를 남깁니다. 예: `--label "1.4.0"` |
| `--interval <초>` | watch | Google 시트를 다시 읽는 간격입니다. 기본값은 30초입니다. |
| `--json` | 모든 명령 | 결과를 JSON으로 출력합니다. 다른 프로그램에서 읽을 때 씁니다. |
| `--force` | init, keygen | 이미 있는 파일을 덮어씁니다. |

전체 옵션은 `holysheet help`로 볼 수 있습니다.

## 오류가 있을 때

문제는 위치, 메시지, 고치는 방법 순서로 출력됩니다. 오류가 있으면 아무것도 쓰지 않고 종료 코드 `1`로 끝납니다.

```text wrap
error: Game.xlsx / Item!F10
  '서른' is not a whole number. Write it like 10, without decimals or thousands separators.
  fix: The column 'Price' is int. Example: 10
failed: 1 errors, 0 warnings. Nothing was written.
```

| 종료 코드 | 의미 |
| --- | --- |
| `0` | 성공 |
| `1` | 시트에 오류가 있거나 검증에 실패했습니다. |
| `2` | 명령이나 옵션을 잘못 적었습니다. |
| `3` | 도구가 실행되지 못했습니다. |

## CI에서 빌드하기

풀 리퀘스트마다 `check`를 실행하면 잘못된 시트가 병합되는 것을 막을 수 있습니다.

```bash
holysheet check              # 오류가 있으면 종료 코드 1로 실패합니다
holysheet build --label "$BUILD_VERSION"
```

Google 시트를 쓰는 프로젝트라면 둘 중 하나를 고릅니다.

- **스냅샷으로 빌드.** 모든 Google 소스에 스냅샷 폴더를 지정해 저장소에 올리고 `--offline`으로 빌드합니다. CI에 자격 증명이 필요 없습니다.
- **직접 읽기.** 접근 방식을 **명령 실행**이나 **액세스 토큰**으로 두고, 자격 증명은 CI의 비밀 값으로 전달합니다.

.NET 도구를 설치하기 어려운 CI에서는 Unity로도 빌드할 수 있습니다.

```bash
Unity -batchmode -quit -projectPath . \
  -executeMethod HolySheet.Editor.HolySheetCommandLine.Build
```

검사만 하려면 끝을 `HolySheetCommandLine.Check`로 바꿉니다.

## 바뀐 내용 비교하기

`diff`는 두 데이터 폴더(또는 매니페스트 파일)를 비교해 어떤 테이블이 바뀌었는지 보여 줍니다. 패치할 파일을 고를 때 씁니다.

```text wrap
$ holysheet diff old/Data new/Data
  ~ Item  (rows 3 -> 3, bytes 125 -> 125)
0 added, 1 changed, 0 removed, 2 unchanged; 125 bytes to fetch.
The layout is the same: clients can load the new data with the code they have.
```

마지막 줄은 열 구성이 그대로인지 알려 줍니다. 그대로라면 이미 배포한 클라이언트가 새 데이터 파일만 받아서 쓸 수 있습니다.
