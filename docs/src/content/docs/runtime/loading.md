---
title: 테이블 불러오기
description: 빌드한 테이블을 Unity와 .NET 서버에서 불러오는 방법을 플랫폼별로 설명합니다.
sidebar:
  order: 1
---

빌드하면 모든 테이블을 담는 클래스(기본 이름 `GameTables`)가 생깁니다. 이 클래스의 `Load`를 한 번 호출하면 모든 테이블이 메모리에 올라옵니다. 게임을 시작할 때 한 번 불러와 계속 쓰면 됩니다.

```csharp
using Game.Data;   // 생성된 코드의 네임스페이스
using HolySheet;

var source = new StreamingAssetsTableSource("Tables");
GameTables tables = GameTables.Load(source);
```

`"Tables"`는 `StreamingAssets` 아래의 폴더 이름입니다. **설정 → 출력 → 데이터**가 `Assets/StreamingAssets/Tables`일 때의 값입니다.

## 플랫폼별 방법

데이터 파일을 어디에 두었고 어떤 플랫폼에서 실행하는지에 따라 **소스**와 **메서드**를 고릅니다.

| 실행 환경 | 소스 | 메서드 |
| --- | --- | --- |
| PC, macOS, iOS, 콘솔 | `new StreamingAssetsTableSource("Tables")` | `Load` |
| Android, WebGL | `new StreamingAssetsTableSource("Tables")` | `LoadAsync` |
| 데이터를 `Assets/Resources/Tables`에 둔 경우 | `new ResourcesTableSource("Tables")` | `Load` |
| .NET 서버 | 데이터 폴더의 경로(문자열) | `Load` |
| 내려받은 파일, Addressables 등 | `TableSource.FromDelegate(…)` | `Load` |

### Android와 WebGL

Android와 WebGL에서는 `StreamingAssets`의 파일을 바로 열 수 없어서, 기다릴 수 있는 `LoadAsync`를 써야 합니다. 이 방법은 다른 플랫폼에서도 똑같이 동작하므로, 여러 플랫폼에 출시한다면 `LoadAsync` 하나로 통일하는 편이 간단합니다.

```csharp
public class GameBoot : MonoBehaviour
{
    public static GameTables Tables { get; private set; }

    async void Start()
    {
        var source = new StreamingAssetsTableSource("Tables");
        Tables = await GameTables.LoadAsync(source);
        // 여기부터 Tables를 쓸 수 있습니다
    }
}
```

`StreamingAssetsTableSource`는 메인 스레드에서 만드세요.

### 직접 파일을 건네주기

패치 서버에서 받은 파일이나 Addressables처럼 HolySheet가 모르는 곳에 데이터가 있으면, 파일 이름을 받아 내용을 돌려주는 함수를 넘깁니다. 파일이 없으면 `null`을 돌려줍니다.

```csharp
TableSource source = TableSource.FromDelegate(fileName =>
{
    string path = Path.Combine(patchFolder, fileName);
    return File.Exists(path) ? File.ReadAllBytes(path) : null;
});

GameTables tables = GameTables.Load(source);
```

## 불러오기에 실패할 때

파일이 없거나, 손상됐거나, 지금 코드와 맞지 않으면 `TableLoadException`이 발생합니다. 일부만 불러온 어중간한 상태는 생기지 않습니다.

```csharp
try
{
    tables = GameTables.Load(source);
}
catch (TableLoadException exception)
{
    // exception.Error로 원인을 구분합니다
    Debug.LogError($"{exception.Error}: {exception.Message}");
}
```

| `exception.Error` | 원인 | 해결 |
| --- | --- | --- |
| `FileMissing` | 매니페스트나 데이터 파일이 없습니다. | 폴더 경로를 확인하고 빌드합니다. |
| `SchemaMismatch` | 데이터가 지금의 생성 코드와 다른 열 구성으로 빌드됐습니다. | 다시 빌드하고 코드와 데이터를 함께 배포합니다. |
| `HashMismatch` | 데이터 파일이 빌드 후에 바뀌었거나 잘렸습니다. | 데이터 폴더를 통째로 다시 배포합니다. |
| `SignatureInvalid` | 서명이 없거나 공개 키와 맞지 않습니다. | [변조 감지와 서명](../integrity/)을 확인합니다. |
| `ManifestInvalid`, `DecodeFailed` | 파일 내용을 해석할 수 없습니다. | 같은 버전의 도구로 다시 빌드합니다. |

## 더 빠르게 불러오기

테이블이 크면 여러 스레드로 나눠 읽을 수 있습니다. 기본값은 꺼져 있습니다.

```csharp
var options = new TableLoadOptions { Parallel = true };
GameTables tables = GameTables.Load(directory, options);
```

- 효과는 테이블이 여러 개이고 클 때, 특히 서버에서 큽니다. 10만 행짜리 테이블 8개(72.6MB)를 서버 GC 설정의 .NET에서 불러오면 91ms가 16ms로 줄어듭니다.
- WebGL처럼 스레드가 없는 플랫폼에서는 켜지 마세요.
- `ResourcesTableSource`는 메인 스레드에서만 읽을 수 있습니다. 병렬로 불러오려면 먼저 메모리로 읽어 둡니다.

  ```csharp
  var resources = new ResourcesTableSource("Tables");
  var source = TableSource.Preload(resources);   // 메인 스레드에서 읽기

  var options = new TableLoadOptions { Parallel = true };
  GameTables tables = GameTables.Load(source, options);
  ```

## 다음 단계

불러온 테이블에서 값을 꺼내는 방법은 [데이터 조회하기](../reading/)에 있습니다.
