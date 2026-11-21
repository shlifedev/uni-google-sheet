---
title: 첫 아이템 테이블 만들기
description: Excel에 입력한 아이템을 HolySheet v2로 빌드하고 Unity에서 가격을 읽어 봅니다.
---

HolySheet는 Excel, CSV, Google Sheets에 작성한 게임 데이터를 C#에서 읽을 수 있는 테이블로 만듭니다. 여기서는 Excel에 ‘나무 검’을 한 줄 입력하고, Unity 콘솔에서 아이템 번호로 이름과 가격을 조회합니다.

## Unity 프로젝트 준비하기

1. Unity 6000.0 이상에서 **Window → Package Management → Package Manager**를 엽니다.
2. **+ → Install package from git URL**을 선택하고 아래 주소를 입력합니다. HolySheet v2 개발 브랜치를 지정한 주소입니다.

   ```text
   https://github.com/shlifedev/uni-google-sheets.git?path=src#dev/feat-2026-remaster
   ```

3. 이 예제는 기본 직렬화 라이브러리인 MemoryPack을 사용합니다. 프로젝트에 추가한 뒤 Unity의 스크립트 컴파일이 끝날 때까지 기다립니다. [MemoryPack의 Unity 설치 안내](https://github.com/Cysharp/MemoryPack#unity)
4. **Tools → HolySheet → Tables**를 열고 **holysheet.json 만들기**를 누릅니다. 프로젝트 폴더에 `Assets`와 나란히 `Sheets` 폴더와 설정 파일이 생깁니다.

## 아이템 입력하기

Excel에서 새 통합 문서를 만들고 시트 탭 이름을 **Item**으로 바꿉니다. A1부터 아래 표처럼 입력하고 `Sheets/Items.xlsx`로 저장합니다.

| `Key<int>` | `string` | `int` |
| --- | --- | --- |
| Id | Name | Price |
| 아이템 번호 | 이름 | 가격 |
| 1001 | 나무 검 | 100 |

첫 줄은 타입, 둘째 줄은 C#에서 쓸 필드 이름, 셋째 줄은 작성자를 위한 설명입니다. 실제 아이템은 넷째 줄부터 시작합니다. `Key<int>`가 붙은 `Id`는 각 아이템을 구별하는 정수입니다.

## 빌드하기

HolySheet 창에서 **검사**를 눌러 시트를 확인한 뒤 **전체 빌드**를 누릅니다. 기본 설정에서는 다음 두 폴더에 결과가 생깁니다.

| 폴더 | 확인할 파일 |
| --- | --- |
| `Assets/Game/Generated/Tables` | `GameTables.g.cs`, `ItemRow.g.cs` 등 C# 스크립트 |
| `Assets/StreamingAssets/Tables` | 아이템 데이터 `Item.bytes`와 빌드에 포함된 파일 목록 |

## Unity 콘솔에서 가격 확인하기

아래 코드를 `Assets/ItemExample.cs`에 저장하고 씬의 게임 오브젝트에 붙입니다. 에디터에서 **Play**를 누르면 콘솔에 **나무 검 · 100골드**가 표시됩니다.

```csharp
using Game.Data;
using HolySheet;
using UnityEngine;

public class ItemExample : MonoBehaviour
{
    void Start()
    {
        var tables = GameTables.Load(new StreamingAssetsTableSource("Tables"));
        var item = tables.Item.Get(1001);
        Debug.Log($"{item.Name} · {item.Price}골드");
    }
}
```

가격을 바꿔 보려면 Play 모드를 종료하고 Excel의 C4 값을 수정하세요. 저장한 뒤 다시 **전체 빌드**하고 실행하면 새 가격을 확인할 수 있습니다.
