---
title: holysheet.json 레퍼런스
description: 설정 파일의 모든 항목과 기본값, 설정 페이지에서의 이름입니다.
sidebar:
  order: 1
---

`holysheet.json`은 프로젝트 폴더(Unity에서는 `Assets`와 나란한 위치)에 있는 설정 파일입니다. Unity 창, 데스크톱 앱, 명령줄 도구가 모두 이 파일을 읽습니다. 보통은 **설정** 페이지에서 고치지만, 직접 고쳐도 됩니다.

파일 안의 경로는 모두 `holysheet.json`이 있는 폴더를 기준으로 합니다.

## 전체 예

```jsonc title="holysheet.json"
{
  "sources": [
    { "type": "xlsx", "path": "Sheets" },
    { "type": "csv", "path": "Sheets/Csv" },
    {
      "type": "google",
      "spreadsheetId": "1AbC…",
      "auth": { "type": "serviceAccount", "keyFile": "secrets/sa.json" },
      "snapshot": "Snapshots/Balance"
    },
    {
      "type": "googleFolder",
      "folderId": "1XyZ…",
      "auth": { "type": "apiKey", "apiKeyEnv": "GOOGLE_API_KEY" }
    }
  ],
  "output": {
    "code": "Assets/Game/Generated/Tables",
    "data": "Assets/StreamingAssets/Tables",
    "csv": null,
    "schema": true
  },
  "codegen": {
    "namespace": "Game.Data",
    "tablesClass": "GameTables",
    "serializer": "memorypack"
  },
  "sheet": {
    "header": ["type", "name", "comment"],
    "arraySeparator": ","
  },
  "csv": { "backend": "auto" },
  "signing": {
    "publicKey": "rsa:…",
    "privateKeyFile": "holysheet.private.key"
  },
  "build": { "label": null, "parallel": true }
}
```

`sources`만 있으면 나머지는 기본값을 씁니다.

## sources

시트를 읽어 올 곳의 목록입니다. 설정 페이지의 **소스**에 해당합니다. [소스 연결하기](../../tools/sources/)

| 항목 | 쓰는 종류 | 설명 |
| --- | --- | --- |
| `type` | 모두 | `xlsx`, `csv`, `google`, `googleFolder` 중 하나 |
| `path` | `xlsx`, `csv` | 파일 또는 폴더의 경로 |
| `spreadsheetId` | `google` | 스프레드시트 URL의 `/d/` 뒤에 있는 id |
| `folderId` | `googleFolder` | Drive 폴더 URL의 `/folders/` 뒤에 있는 id |
| `auth` | `google`, `googleFolder` | 접근 방식. 아래 표를 보세요. |
| `sheets` | `google` | 읽을 탭의 목록. `[{ "name": "Item", "gid": 0 }]`. 없으면 모든 탭을 읽습니다. |
| `snapshot` | `google`, `googleFolder` | 읽어 온 시트를 CSV로 저장할 폴더 |
| `name` | `google`, `googleFolder` | 메시지와 목록에 표시할 이름. 없으면 문서의 제목을 쓰고, 제목을 알 수 없으면 id를 씁니다. |

### auth

| `type` | 설정 페이지 | 함께 적는 항목 |
| --- | --- | --- |
| `public` | 공개 링크 | 없음 |
| `apiKey` | API 키 | `apiKeyEnv`(키가 든 환경 변수의 이름) 또는 `apiKey`(키 자체) |
| `serviceAccount` | 서비스 계정 | `keyFile`(JSON 키 파일의 경로) |
| `accessToken` | 액세스 토큰 | `accessTokenEnv`(토큰이 든 환경 변수의 이름) |
| `command` | 명령 실행 | `command`(자격 증명을 출력하는 명령) |
| `custom` | 커스텀 프로바이더 | `provider`(C# 클래스의 이름). Unity 에디터 전용 |

`apiKey`에 키를 직접 적으면 `holysheet.json`에 그대로 저장되어 저장소에 올라갑니다. `apiKeyEnv`를 권합니다.

## output

결과를 쓸 곳입니다. 설정 페이지의 **출력**에 해당합니다.

| 항목 | 설정 페이지 | 기본값 | 설명 |
| --- | --- | --- | --- |
| `code` | 코드 | `Generated/Code` | `.g.cs` 파일을 쓸 폴더. Unity에서는 `Assets` 아래여야 컴파일됩니다. |
| `data` | 데이터 | `Generated/Data` | `.bytes` 파일과 매니페스트를 쓸 폴더 |
| `csv` | 변환된 CSV | 없음 | 지정하면 시트 규칙을 적용한 뒤의 테이블을 CSV로도 씁니다. |
| `schema` | 스키마 파일 쓰기 | `true` | 데이터 옆에 `tables.schema.json`을 씁니다. 생성된 코드 없이 열 구성을 알아야 하는 도구를 위한 파일입니다. |

Unity에서 **holysheet.json 만들기**로 만들면 `code`는 `Assets/Game/Generated/Tables`, `data`는 `Assets/StreamingAssets/Tables`로 설정됩니다.

## codegen

생성되는 코드에 관한 설정입니다. 설정 페이지의 **출력** 아래쪽에 있습니다.

| 항목 | 설정 페이지 | 기본값 | 설명 |
| --- | --- | --- | --- |
| `namespace` | 네임스페이스 | `Game.Data` | 생성된 클래스의 네임스페이스 |
| `tablesClass` | 테이블 클래스 | `GameTables` | 모든 테이블을 담는 클래스의 이름 |
| `serializer` | 직렬화 | `memorypack` | `memorypack` 또는 `messagepack` |

## sheet, csv, build

설정 페이지의 **시트와 빌드 옵션**에 해당합니다.

| 항목 | 설정 페이지 | 기본값 | 설명 |
| --- | --- | --- | --- |
| `sheet.header` | 헤더 행 | `["type", "name", "comment"]` | 헤더 행의 구성. [헤더 행 바꾸기](../../sheets/basics/#헤더-행-바꾸기) |
| `sheet.arraySeparator` | 배열 구분자 | `,` | 배열 값을 나누는 글자 하나 |
| `csv.backend` | CSV 리더 | `auto` | CSV를 읽는 방식. `auto`, `builtin`, `sep`. 결과는 모두 같습니다. |
| `build.parallel` | 병렬 처리 | `true` | 여러 스레드로 빌드합니다. 결과는 같습니다. |
| `build.label` | 빌드 라벨 | 없음 | 매니페스트에 남길 버전 표시 |
| `build.cache` | (없음) | `.holysheet/cache` | [선택 빌드](../../tools/partial-build/)가 쓰는 캐시 폴더 |

## signing

설정 페이지의 **서명 (…)** 묶음에 해당합니다. [변조 감지와 서명](../../runtime/integrity/#서명)

| 항목 | 설정 페이지 | 설명 |
| --- | --- | --- |
| `publicKey` | 공개 키 | 값이 있으면 빌드할 때 서명하고, 게임은 서명된 데이터만 불러옵니다. |
| `privateKeyFile` | 개인 키 파일 | 개인 키 파일의 경로. 저장소에 올리지 않습니다. |
| `privateKeyEnv` | 개인 키 환경 변수 | 개인 키의 내용이 든 환경 변수의 이름. CI에서 씁니다. 파일보다 먼저 씁니다. |

## 파일에 저장되지 않는 설정

창의 언어, **보기** 메뉴의 표 모양, 데스크톱 앱의 테마와 자동 빌드 주기는 `holysheet.json`에 들어가지 않습니다. 사람마다 다른 설정이라서 각자의 컴퓨터에만 저장됩니다. Unity에서는 프로젝트의 `UserSettings` 폴더에 저장됩니다.
