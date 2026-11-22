---
title: 데이터 조회하기
description: 불러온 테이블에서 키로 행을 찾고, 목록을 돌고, 참조를 따라가는 방법입니다.
sidebar:
  order: 2
---

불러온 `GameTables`에는 테이블마다 속성이 하나씩 있습니다. `Item` 시트는 `tables.Item`이 되고, 그 안의 한 행은 `ItemRow`입니다. 이 페이지의 예제는 다음 시트를 기준으로 합니다.

| `Key<int>` | `Key<string>` | `string` | `Enum<EItemKind>` | `Index<int>` | `int` | `float[]` |
| --- | --- | --- | --- | --- | --- | --- |
| Id | Code | Name | Kind | Tier | Price | Stats |
| 아이템 번호 | 코드 | 이름 | 종류 | 등급 | 가격 | 능력치 목록 |
| 1001 | wooden_sword | 나무 검 | Weapon | 1 | 100 | 3, 1.5 |
| 1002 | leather_armor | 가죽 갑옷 | Armor | 1 | 150 | 5 |

## 키로 한 행 찾기

찾는 행이 없을 때 어떻게 할지에 따라 고릅니다.

| 메서드 | 행이 없을 때 | 이런 때 씁니다 |
| --- | --- | --- |
| `Get(1001)` | 예외가 발생합니다. | 반드시 있어야 하는 행. 없으면 데이터 오류입니다. |
| `Find(1001)` | `null`을 돌려줍니다. | 없을 수도 있는 행 |
| `TryGet(1001, out row)` | `false`를 돌려줍니다. | `if`로 바로 분기할 때 |
| `Contains(1001)` | `false`를 돌려줍니다. | 있는지만 확인할 때 |

```csharp
ItemRow sword = tables.Item.Get(1001);
Debug.Log($"{sword.Name} · {sword.Price}골드");   // 나무 검 · 100골드

ItemRow? maybe = tables.Item.Find(9999);          // null

if (tables.Item.TryGet(1002, out ItemRow armor))
    Debug.Log(armor.Name);                        // 가죽 갑옷
```

두 번째 키부터는 메서드 이름 뒤에 필드 이름이 붙습니다.

```csharp
ItemRow sword = tables.Item.GetByCode("wooden_sword");
ItemRow? none = tables.Item.FindByCode("no_such_item");
```

## 여러 행 읽기

```csharp
// 모든 행을 시트에 적힌 순서대로
foreach (ItemRow item in tables.Item.Rows)
    Debug.Log(item.Name);

int count = tables.Item.Count;                 // 행의 수

// Index<int> Tier 열에서 생긴 메서드: 등급이 1인 아이템 모두
foreach (ItemRow item in tables.Item.ListByTier(1))
    Debug.Log(item.Name);
```

`ListByTier`는 해당하는 행이 없으면 빈 목록을 돌려줍니다.

## 배열과 enum 읽기

배열 열은 `Length`와 `[순서]`로 읽고, `foreach`로 돌 수 있습니다.

```csharp
float first = sword.Stats[0];        // 3
int howMany = sword.Stats.Length;    // 2

if (sword.Kind == EItemKind.Weapon) { /* … */ }
```

여러 개를 고르는 enum은 `HasFlag`로 확인합니다. [enum 정의하기](../../sheets/enums/#열의-타입으로-쓰기)에 예제가 있습니다.

## 참조 따라가기

`Ref<Item>` 열의 값은 `Item` 테이블의 `Get`이나 `Find`에 그대로 넘깁니다.

| 시트의 타입 | 코드에서 읽는 방법 |
| --- | --- |
| `Ref<Item>` | `tables.Item.Get(row.Weapon)` |
| `Ref<Item>?` | `tables.Item.Find(row.Rare)`. 비어 있으면 `null`입니다. `row.Rare.HasValue`로 확인할 수도 있습니다. |
| `Ref<Item>[]` | 키의 목록입니다. `foreach`로 돌며 `tables.Item.Get(id)`를 호출합니다. |

```csharp
MonsterRow goblin = tables.Monster.Get((int)EMonsterId.Goblin);

foreach (int itemId in goblin.Drops)
    Debug.Log(tables.Item.Get(itemId).Name);     // 나무 검, 체력 물약

ItemRow? rare = tables.Item.Find(goblin.Rare);   // 가죽 갑옷
```

## 설정 테이블 읽기

[설정 테이블](../../sheets/splitting/#설정-테이블)은 행이 하나뿐이므로 `Value`로 바로 읽습니다.

```csharp
int maxLevel = tables.Game.Value.MaxLevel;
ItemRow start = tables.Item.Get(tables.Game.Value.StartItem);
```

## 테이블은 바꿀 수 없습니다

불러온 테이블은 읽기 전용입니다. 값을 바꾸는 코드는 컴파일되지 않습니다.

```csharp
sword.Price = 1;        // 컴파일 오류: 설정자가 없습니다
sword.Stats[0] = 1f;    // 컴파일 오류: 배열도 읽기 전용입니다
```

게임 도중 바뀌는 값(강화 수치가 붙은 아이템, 현재 체력)은 테이블에 넣지 않고, 테이블의 값을 읽어 만든 별도의 객체에 둡니다. 덕분에 어떤 코드도 원본 데이터를 실수로 망가뜨릴 수 없고, 여러 스레드에서 동시에 읽어도 안전합니다.

## 조회 기능 덧붙이기

생성된 테이블 클래스는 `partial`이라서, 다른 파일에 메서드를 덧붙일 수 있습니다. 생성된 파일은 고치지 마세요.

```csharp title="Assets/Game/ItemTable.Extra.cs"
namespace Game.Data
{
    public sealed partial class ItemTable
    {
        public ItemRow? MostExpensive { get; private set; }

        // 테이블을 불러온 직후에 한 번 호출됩니다
        partial void OnLoaded()
        {
            foreach (ItemRow item in Rows)
            {
                if (MostExpensive == null || item.Price > MostExpensive.Price)
                    MostExpensive = item;
            }
        }
    }
}
```
