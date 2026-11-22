---
title: 시트 기본 구조
description: 시트 한 장이 테이블 하나가 되는 규칙, 헤더 세 줄, 빌드에서 제외하는 방법입니다.
sidebar:
  order: 1
---

시트 한 장이 테이블 하나입니다. 위쪽 세 줄에 타입, 이름, 설명을 적고 넷째 줄부터 데이터를 채웁니다. Excel, CSV, Google Sheets 모두 같은 규칙을 씁니다.

## 시트의 구성

<div class="hs-sheet not-content">
<table>
<thead><tr><th></th><th>A</th><th>B</th><th>C</th><th>D</th><th>E</th><th class="hs-note"></th></tr></thead>
<tbody>
<tr class="hs-head"><th>1</th><td>Key&lt;int&gt;</td><td>string</td><td>Enum&lt;EItemKind&gt;</td><td>int</td><td>#note</td><td class="hs-note">타입</td></tr>
<tr class="hs-head"><th>2</th><td>Id</td><td>Name</td><td>Kind</td><td>Price</td><td></td><td class="hs-note">필드 이름</td></tr>
<tr class="hs-head"><th>3</th><td>아이템 번호</td><td>이름</td><td>종류</td><td>가격</td><td>기획 메모</td><td class="hs-note">설명</td></tr>
<tr><th>4</th><td>1001</td><td>나무 검</td><td>Weapon</td><td>100</td><td>시작 장비</td><td class="hs-note">데이터</td></tr>
<tr><th>5</th><td>1002</td><td>가죽 갑옷</td><td>Armor</td><td>150</td><td></td><td class="hs-note"></td></tr>
<tr class="hs-skip"><th>6</th><td>~9001</td><td>용의 검</td><td>Weapon</td><td>0</td><td>작업 중</td><td class="hs-note">제외된 행</td></tr>
</tbody>
</table>
<div class="hs-tabs"><span class="hs-on">Item</span><span>Monster</span><span>#기획메모</span><em>← 탭 이름이 테이블 이름</em></div>
</div>

| 위치 | 적는 내용 | 생성되는 코드 |
| --- | --- | --- |
| 탭 이름 | 테이블 이름. CSV에서는 파일 이름(`Item.csv`)입니다. | `tables.Item`, `ItemRow` |
| 1행 | 열의 [타입](../types/) | 필드의 C# 타입 |
| 2행 | 필드 이름 | `item.Name`, `item.Price` |
| 3행 | 설명. 비워도 됩니다. | 필드의 주석. 코드 편집기에서 필드에 마우스를 올리면 보입니다. |
| 4행부터 | 데이터. 한 행이 아이템 하나입니다. | `ItemRow` 하나 |

## 이름 짓기

테이블 이름과 필드 이름은 C# 코드의 이름이 됩니다.

- 글자나 밑줄(`_`)로 시작하고, 글자·숫자·밑줄만 씁니다. 공백과 기호는 쓸 수 없습니다.
- `int`, `class` 같은 C# 예약어는 쓸 수 없습니다.
- 한 테이블 안에서 대소문자만 다른 이름(`price`와 `Price`)은 함께 쓸 수 없습니다.

한글 이름도 쓸 수 있지만, 코드에서 그 이름을 그대로 입력해야 합니다. 이 문서의 예제는 이름을 영문으로 적고 한글은 설명 행에 적습니다.

## 빈 셀과 빈 행

- 빈 셀은 그 타입의 기본값이 됩니다. 숫자는 `0`, `bool`은 `false`, `string`은 빈 문자열입니다.
- 키 열은 비울 수 없습니다.
- 완전히 빈 행은 건너뜁니다. 중간에 빈 줄을 넣어 묶음을 구분해도 됩니다.

## 빌드에서 제외하기

이름이나 값 앞에 `#` 또는 `~`를 붙이면 그 부분은 읽지 않습니다. 메모를 남기거나 작업 중인 데이터를 잠시 빼 둘 때 씁니다.

| 제외할 대상 | 방법 | 예 |
| --- | --- | --- |
| 시트 전체 | 탭 이름을 `#`이나 `~`로 시작합니다. | `#기획메모` |
| 열 하나 | 그 열의 헤더 셀 중 하나를 `#`이나 `~`로 시작합니다. | 타입 칸에 `#note` |
| 행 하나 | 그 행의 A열 값을 `#`이나 `~`로 시작합니다. | `1002` → `~1002` |

행을 다시 포함하려면 A열 값을 원래대로 되돌리고 빌드합니다.

## 시트 이름의 종류

탭 이름의 형태에 따라 시트의 역할이 정해집니다. `Item`을 예로 들면 다음과 같습니다.

| 탭 이름 | 역할 | 설명 |
| --- | --- | --- |
| `Item` | 테이블 | 이 페이지에서 설명한 기본 형태입니다. |
| `Item.Weapons` | 행 추가 | `Item` 테이블에 행을 더합니다. [시트 나누기](../splitting/) |
| `Item.Col.Balance` | 열 추가 | `Item` 테이블에 열을 더합니다. [시트 나누기](../splitting/) |
| `Game.Config` | 설정 테이블 | 값이 한 벌뿐인 테이블입니다. [시트 나누기](../splitting/#설정-테이블) |
| `Enums`, `Battle.Enums` | enum 정의 | 선택지 목록을 정의합니다. [enum 정의하기](../enums/) |
| `#메모`, `~임시` | 제외 | 읽지 않습니다. |

## 헤더 행 바꾸기

기본 헤더는 위에서부터 타입, 이름, 설명 순서입니다. 순서를 바꾸거나 설명 행을 없애려면 **설정 → 시트와 빌드 옵션 → 헤더 행**에서 고릅니다.

| 헤더 행 | 시트의 모습 |
| --- | --- |
| `type, name, comment` | 기본값. 타입, 이름, 설명 순서이고 4행부터 데이터입니다. |
| `comment, name, type` | 설명을 맨 위에 적습니다. |
| `type, name` | 설명 행이 없습니다. 3행부터 데이터입니다. |

이 설정은 프로젝트의 모든 시트에 똑같이 적용됩니다.
