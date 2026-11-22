---
title: 키, 인덱스, 참조
description: 행을 찾는 키, 같은 값끼리 모으는 인덱스, 다른 테이블의 행을 가리키는 참조를 시트에 적는 방법입니다.
sidebar:
  order: 3
---

타입을 `Key<>`, `Index<>`, `Ref<>`로 감싸면 그 열에 역할이 생깁니다. 게임 코드에서 행을 어떻게 찾을지, 테이블끼리 어떻게 연결할지를 시트에서 정합니다.

| 타입 | 역할 | 값이 겹쳐도 되나요 | 생기는 조회 방법 |
| --- | --- | --- | --- |
| `Key<int>` | 행 하나를 찾습니다. | 안 됩니다. | `tables.Item.Get(1001)` |
| `Index<int>` | 같은 값을 가진 행을 모읍니다. | 됩니다. | `tables.Item.ListByTier(1)` |
| `Ref<Item>` | 다른 테이블의 행을 가리킵니다. | 됩니다. | `tables.Item.Get(monster.Rare)` |

## 키

키는 행을 구별하는 값입니다. 아이템 번호처럼 겹치지 않는 값을 가진 열의 타입을 `Key<>`로 감쌉니다.

| `Key<int>` | `Key<string>` | `string` | `int` |
| --- | --- | --- | --- |
| Id | Code | Name | Price |
| 아이템 번호 | 코드 | 이름 | 가격 |
| 1001 | wooden_sword | 나무 검 | 100 |
| 1002 | leather_armor | 가죽 갑옷 | 150 |

- 키로 쓸 수 있는 타입은 정수, `string`, enum입니다. `float`, `DateTime`, 배열은 쓸 수 없습니다.
- 같은 값을 두 번 쓰거나 키 칸을 비우면 검사에서 오류가 납니다.
- 키를 여러 개 둘 수 있습니다. 가장 왼쪽 키가 **기본 키**이고, 나머지는 필드 이름이 붙은 조회 방법이 생깁니다.

```csharp
ItemRow sword = tables.Item.Get(1001);                  // 기본 키
ItemRow same  = tables.Item.GetByCode("wooden_sword");  // 두 번째 키
```

키가 없는 테이블도 만들 수 있습니다. 이때는 `tables.Item.Rows`로 모든 행을 순서대로 읽습니다.

## 인덱스

같은 값을 가진 행을 한꺼번에 가져오려면 `Index<>`를 씁니다. 값이 겹쳐도 됩니다.

| `Key<int>` | `string` | `Index<int>` |
| --- | --- | --- |
| Id | Name | Tier |
| 아이템 번호 | 이름 | 등급 |
| 1001 | 나무 검 | 1 |
| 1002 | 가죽 갑옷 | 1 |
| 1003 | 철 검 | 2 |

```csharp
var tierOne = tables.Item.ListByTier(1);   // 나무 검, 가죽 갑옷
```

해당하는 행이 없으면 빈 목록을 돌려줍니다.

## 참조

다른 테이블의 행을 가리키려면 `Ref<테이블이름>`을 쓰고, 셀에는 그 테이블의 기본 키 값을 적습니다. 아래 `Monster` 시트는 몬스터가 떨어뜨리는 아이템을 `Item` 테이블의 번호로 적었습니다.

| `Key<int>` | `string` | `Ref<Item>[]` | `Ref<Item>?` |
| --- | --- | --- | --- |
| Id | Name | Drops | Rare |
| 몬스터 번호 | 이름 | 떨어뜨리는 아이템 | 희귀 드롭 |
| 1 | 슬라임 | 2001 | |
| 2 | 고블린 | 1001, 2001 | 1002 |

| 타입 | 의미 | 빈 셀 |
| --- | --- | --- |
| `Ref<Item>` | 아이템 하나. 반드시 있어야 합니다. | 오류 |
| `Ref<Item>?` | 아이템 하나. 없을 수도 있습니다. | 가리키는 행 없음 |
| `Ref<Item>[]` | 아이템 여러 개 | 빈 목록 |

참조의 가장 큰 장점은 **빌드할 때 확인된다**는 점입니다. `Item` 테이블에 없는 번호를 적으면 검사가 그 셀을 짚어 줍니다. 아이템을 지웠는데 몬스터가 여전히 그 아이템을 떨어뜨리는 실수를 게임을 실행하기 전에 잡습니다.

```text
error: Game.xlsx / Monster!F7
  The table 'Item' has no row with key '7777'.
  fix: Add the row to 'Item', or fix the key here.
```

게임 코드에서는 참조를 그 테이블의 `Get`이나 `Find`에 넘겨 행을 얻습니다.

```csharp
MonsterRow goblin = tables.Monster.Get(2);

// Ref<Item>[]는 키의 목록입니다
foreach (int itemId in goblin.Drops)
    Debug.Log(tables.Item.Get(itemId).Name);

// Ref<Item>?는 비어 있으면 null을 돌려줍니다
ItemRow? rare = tables.Item.Find(goblin.Rare);
```

조회 방법은 [데이터 조회하기](../../runtime/reading/)에서 자세히 다룹니다.
