---
title: 실행 중에 다시 불러오기
description: 게임이나 서버를 끄지 않고 시트 수정 내용을 반영하는 방법입니다.
sidebar:
  order: 3
---

`Watch`는 데이터 폴더를 지켜보다가 파일이 바뀌면 테이블을 다시 불러옵니다. 시트를 고치고 빌드하면, Play 모드를 끄거나 서버를 재시작하지 않아도 새 값이 반영됩니다. 수치를 조금씩 바꿔 가며 밸런스를 잡을 때 특히 편합니다.

<div class="hs-flow not-content">
  <div><strong>시트 저장</strong><span>Excel에서 값을 고치고 저장합니다.</span></div>
  <div class="hs-accent"><strong>자동 빌드</strong><span>창의 <b>자동 빌드</b>나 <code>holysheet watch</code>가 데이터 파일을 새로 씁니다.</span></div>
  <div><strong>다시 불러오기</strong><span>실행 중인 게임의 <code>Watch</code>가 바뀐 테이블을 읽습니다.</span></div>
</div>

## 사용하기

`Load` 대신 `Watch`를 호출하고, 테이블은 항상 `watcher.Current`에서 읽습니다.

```csharp
using Game.Data;
using HolySheet;
using UnityEngine;

public class GameData : MonoBehaviour
{
    TableWatcher<GameTables> _watcher;

    // 항상 최신 테이블
    public GameTables Tables => _watcher.Current;

    void Awake()
    {
        var source = new StreamingAssetsTableSource("Tables");
        _watcher = GameTables.Watch(source.Directory);

        _watcher.Reloaded += change =>
        {
            string names = string.Join(", ", change.ChangedTables);
            Debug.Log("바뀐 테이블: " + names);
        };
        _watcher.ReloadFailed += exception =>
            Debug.LogWarning("다시 불러오지 못함: " + exception.Message);
    }

    // 지켜보기를 멈춥니다
    void OnDestroy() => _watcher.Dispose();
}
```

| 이름 | 설명 |
| --- | --- |
| `watcher.Current` | 가장 최근에 불러온 테이블입니다. 어느 스레드에서 읽어도 안전합니다. |
| `Reloaded` | 새 테이블로 바뀐 직후에 호출됩니다. `change.ChangedTables`에 바뀐 테이블의 이름이 들어 있습니다. |
| `ReloadFailed` | 바뀐 데이터를 불러오지 못했을 때 호출됩니다. 이때 `Current`는 이전 테이블 그대로입니다. |
| `Dispose()` | 지켜보기를 멈춥니다. |

Unity에서는 두 이벤트가 메인 스레드에서 호출되므로, 이벤트 안에서 UI를 고쳐도 됩니다.

## 알아 둘 점

**행을 오래 들고 있지 마세요.** 다시 불러오면 `Current`가 새 테이블로 바뀌지만, 이전에 꺼내 둔 `ItemRow`는 옛 값을 그대로 가지고 있습니다. 필드에 행을 저장해 두지 말고, 필요할 때마다 `Tables.Item.Get(id)`로 다시 꺼내세요. 화면을 갱신해야 하면 `Reloaded`에서 합니다.

**중간 상태는 보이지 않습니다.** 새 테이블을 따로 다 만든 뒤 한 번에 교체합니다. 일부 테이블만 새 값인 순간은 없습니다. 새 데이터에 문제가 있으면 교체하지 않고 이전 테이블을 계속 씁니다.

**바뀐 테이블만 다시 읽습니다.** 시트 하나를 고쳤으면 테이블 하나만 다시 해석하고, 나머지는 이전 것을 그대로 씁니다.

## 쓸 수 있는 곳

`Watch`는 데이터가 디스크의 일반 파일일 때 동작합니다.

| 환경 | 쓸 수 있나요 |
| --- | --- |
| Unity 에디터, PC와 macOS 빌드 | 예 |
| .NET 서버 | 예 |
| Android, WebGL의 `StreamingAssets` | 아니요. 파일이 패키지 안에 들어 있습니다. |

Android나 WebGL에서 패치로 받은 새 데이터를 적용하려면 `Reload`를 직접 호출합니다. 새 테이블 묶음을 돌려주며, 바뀌지 않은 테이블은 그대로 이어 씁니다.

```csharp
TableSource source = TableSource.FromDirectory(patchFolder);
GameTables updated = tables.Reload(source);
```

## 확인 방식 바꾸기

기본값은 파일 변경 알림을 쓰고, 알림을 쓸 수 없는 플랫폼에서는 1초마다 확인합니다. 스레드가 없는 플랫폼에서는 직접 확인하도록 바꿉니다.

```csharp
var options = new TableWatchOptions { Mode = TableWatchMode.Manual };
var watcher = GameTables.Watch(directory, options);

// 원하는 시점에, 예를 들어 Update에서
watcher.CheckNow();
```
