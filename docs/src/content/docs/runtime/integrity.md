---
title: 변조 감지와 서명
description: 데이터 파일이 손상되거나 바뀌었을 때 알아채는 방법과, 서명으로 바꿔치기를 막는 방법입니다.
sidebar:
  order: 4
---

HolySheet는 데이터 파일을 불러올 때마다 빌드했을 때와 같은 파일인지 확인합니다. 보호는 두 단계입니다.

| 단계 | 막는 것 | 설정 |
| --- | --- | --- |
| 해시 확인 | 파일 손상, 일부 파일만 바뀐 경우, 다른 빌드의 파일이 섞인 경우 | 필요 없습니다. 기본으로 켜져 있습니다. |
| 서명 | 데이터 파일과 파일 목록을 함께 바꿔치기하는 경우 | 키 쌍을 만들어야 합니다. |

## 해시 확인

빌드하면 데이터 폴더에 `tables.manifest.json`이 생깁니다. 이 파일에는 데이터 파일마다 크기와 해시(SHA-256)가 적혀 있습니다. 불러올 때 모든 파일을 이 목록과 비교하고, 하나라도 다르면 불러오지 않습니다.

```json title="tables.manifest.json"
{
  "holysheet": 1,
  "serializer": "memorypack",
  "schema": "3267…",
  "content": "1df2…",
  "tool": "holysheet/2.0.0",
  "tables": [
    { "name": "Item", "file": "Item.bytes", "rows": 10,
      "bytes": 446, "hash": "b92e…", "schema": "0dc3…" }
  ]
}
```

다른 파일이 있으면 `TableLoadException`이 발생하고, `Error`로 원인을 구분합니다.

| `Error` | 의미 |
| --- | --- |
| `HashMismatch` | 데이터 파일이 수정됐거나, 잘렸거나, 다른 빌드의 파일입니다. |
| `SchemaMismatch` | 데이터가 지금의 코드와 다른 열 구성으로 빌드됐습니다. 코드와 데이터를 함께 배포해야 합니다. |

## 서명

해시는 실수와 손상을 잡지만, 누군가 데이터 파일과 `tables.manifest.json`을 함께 고치면 알아챌 수 없습니다. 이것까지 막으려면 매니페스트에 서명합니다.

<div class="hs-flow not-content">
  <div><strong>개인 키</strong><span>빌드하는 컴퓨터에만 둡니다. 매니페스트에 서명합니다.</span></div>
  <div class="hs-accent"><strong>서명된 매니페스트</strong><span>데이터와 함께 배포됩니다.</span></div>
  <div><strong>공개 키</strong><span>생성된 코드에 들어가 게임과 함께 배포됩니다. 서명을 확인합니다.</span></div>
</div>

### 서명 켜기

1. **설정** 페이지에서 **서명 (서명 없는 빌드)** 묶음을 펼치고 **키 쌍 만들기**를 누릅니다.
2. 프로젝트 폴더에 `holysheet.private.key`가 생기고, 공개 키가 `holysheet.json`에 저장됩니다.
3. **전체 빌드**를 누릅니다. 매니페스트에 서명이 붙고, 공개 키가 생성된 코드에 들어갑니다.

이제 묶음의 제목이 **서명 (서명된 빌드)**로 바뀝니다. 이 코드로 만든 게임은 서명이 없거나 맞지 않는 데이터를 불러오지 않고 `SignatureInvalid` 오류를 냅니다.

명령줄에서는 다음과 같습니다.

```bash
# 키 쌍을 만들고 공개 키를 holysheet.json에 적습니다
holysheet keygen --write-config

# 서명해서 빌드합니다
holysheet build

# 데이터 폴더의 해시와 서명을 확인합니다
holysheet verify Generated/Data
```

:::danger
`holysheet.private.key`는 저장소에 올리지 마세요. `.gitignore`에 추가합니다. 개인 키를 가진 사람은 누구나 유효한 데이터를 만들 수 있습니다.
:::

### 팀과 CI에서 쓰기

서명을 켜면 **개인 키가 있는 곳에서만 빌드할 수 있습니다.** 개인 키 없이 빌드하면 서명 없는 데이터가 만들어져 게임이 거부할 것이므로, 빌드 자체가 오류로 멈춥니다.

- **CI에서 빌드한다면** 개인 키의 내용을 CI의 비밀 값으로 등록하고, 서명 묶음의 **개인 키 환경 변수**에 그 환경 변수의 이름을 적습니다. 환경 변수가 있으면 파일보다 먼저 씁니다.
- **개인 키가 없는 컴퓨터에서는 빌드할 수 없습니다.** 누가 어디에서 빌드할지 정한 뒤에 서명을 켜세요.

### 확인하고 끄기

- **데이터 폴더 검증**은 데이터 폴더의 모든 파일을 매니페스트와 대조하고 서명을 확인합니다. 배포 전에 눌러 보세요.
- **서명 중단**은 설정에서 서명 정보를 지웁니다. 다음 빌드부터 서명 없이 빌드합니다.

## 패치에 활용하기

HolySheet에는 패치 기능이 없지만, 패치 시스템을 만들 때 필요한 정보를 매니페스트에 남깁니다.

| 항목 | 쓰임새 |
| --- | --- |
| `content` | 이 데이터 전체를 가리키는 해시입니다. 서버와 클라이언트의 데이터가 같은지 한 값으로 비교합니다. |
| `schema` | 모든 테이블의 열 구성을 가리키는 해시입니다. 생성된 코드의 `GameTables.SchemaHash`와 같아야 불러올 수 있습니다. |
| 테이블마다의 `hash` | 어떤 파일을 새로 받아야 하는지 알 수 있습니다. |
| `build` | `--label`이나 **빌드 라벨**로 남긴 버전 표시입니다. |

두 매니페스트를 비교하는 기능도 있습니다. `RequiresNewCode`가 `false`이면 이미 설치된 게임이 새 데이터 파일만 받아서 쓸 수 있습니다.

```csharp
TableManifestDiff diff = TableManifest.Diff(installed, latest);

foreach (string table in diff.Changed)
    Debug.Log("새로 받을 테이블: " + table);

if (diff.RequiresNewCode)
    Debug.Log("열 구성이 바뀌었습니다. 앱 업데이트가 필요합니다.");
```

명령줄에서는 [`holysheet diff`](../../tools/cli/#바뀐-내용-비교하기)로 같은 비교를 합니다.
