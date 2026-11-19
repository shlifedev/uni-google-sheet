#!/usr/bin/env python3
"""Writes the demo data set: the tables of a mid-sized online RPG, split the way a team would split them.

    python3 tools/make-demo-workbooks.py src/Samples~/Demo/Sheets

Needs openpyxl (pip install openpyxl). The output is checked in; run this only to change it. Output is deterministic.

  Common.xlsx      Enums, Game.Config, #ReadMe
  Character.xlsx   Job (+ Job.Advanced), Level, Skill
  Item.xlsx        Item (+ Item.Material, Item.Equipment), Equipment (+ Equipment.Col.Upgrade), SetBonus, Enhance
  World.xlsx       Region, Map (+ Map.Dungeon), Portal, Npc
  Monster.xlsx     Battle.Enums, Monster (+ Monster.Boss), Spawn, Drop
  Quest.xlsx       Quest (+ Quest.Daily), QuestObjective
  Shop.xlsx        Shop, ShopGoods, Gacha
  Localization/Text.csv

Every rule UGS supports appears somewhere: two Enums sheets, a Flags enum with a value column, a Config sheet, child sheets
that add rows, a .Col sheet that adds columns, Mapping columns, keys, indexes, references across workbooks (required,
optional and arrays), arrays of every kind, ignored sheets, columns and rows. All names are made up for this demo.
"""
import csv
import datetime
import os
import random
import sys

from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill

HEADER_FILL = PatternFill("solid", fgColor="DDEBF7")
BOLD = Font(bold=True)
random.seed(20261118)


def sheet(workbook, name, header, rows, header_rows=3, widths=14):
    ws = workbook.create_sheet(name)
    for row in header + rows:
        ws.append(row)
    for index in range(1, min(header_rows, ws.max_row) + 1):
        for cell in ws[index]:
            cell.fill = HEADER_FILL
            cell.font = BOLD
    for column in ws.columns:
        ws.column_dimensions[column[0].column_letter].width = widths
    ws.freeze_panes = ws.cell(row=header_rows + 1, column=1) if header_rows else None
    return ws


def table(workbook, name, columns, rows):
    """columns: list of (type, name, comment)."""
    header = [[c[0] for c in columns], [c[1] for c in columns], [c[2] for c in columns]]
    return sheet(workbook, name, header, rows)


def book():
    workbook = Workbook()
    workbook.remove(workbook.active)
    return workbook


def save(workbook, folder, name):
    path = os.path.join(folder, name)
    workbook.save(path)
    print("wrote", path)


def common(folder):
    wb = book()
    # One column per enum; a column right of an enum with no name in the name row holds that enum's values.
    enums = [
        ("byte", "EStat", "능력치", ["Str", "Dex", "Int", "Luk", "MaxHp", "MaxMp", "Atk", "MAtk", "Def", "MDef", "Crit", "Speed"]),
        ("byte", "EElement", "속성", ["None", "Fire", "Water", "Earth", "Wind", "Light", "Dark"]),
        ("byte", "ERarity", "등급", ["Common", "Uncommon", "Rare", "Epic", "Legendary"]),
        ("byte", "EItemType", "아이템 종류", ["Consumable", "Material", "Equipment", "Quest", "Box"]),
        ("byte", "EEquipSlot", "장착 부위", ["Weapon", "Head", "Body", "Legs", "Hands", "Feet", "Ring", "Necklace"]),
        ("byte", "EWeaponType", "무기 종류", ["None", "Sword", "Axe", "Staff", "Wand", "Bow", "Crossbow", "Dagger", "Claw"]),
    ]
    flags = ("Flags<int>", "EItemTag", "아이템 속성(복수 선택)", [("None", "0"), ("Tradable", "1 << 0"), ("Stackable", "1 << 1"),
                                                         ("Sellable", "1 << 2"), ("Bound", "1 << 3"), ("Event", "1 << 4")])
    more = [
        ("byte", "EMapType", "맵 종류", ["Town", "Field", "Dungeon", "Raid"]),
        ("byte", "EMonsterRank", "몬스터 등급", ["Normal", "Elite", "Boss", "RaidBoss"]),
        ("byte", "EQuestType", "퀘스트 종류", ["Main", "Side", "Daily", "Weekly"]),
        ("byte", "EObjective", "목표 종류", ["Kill", "Collect", "Talk", "Reach"]),
        ("byte", "ECurrency", "재화", ["Gold", "Gem", "Honor"]),
    ]
    types, names, comments, columns = [], [], [], []
    for underlying, name, comment, members in enums:
        types.append(underlying); names.append(name); comments.append(comment); columns.append(members)
    types += [flags[0], None]; names += [flags[1], None]; comments += [flags[2], "값"]
    columns.append([m for m, _ in flags[3]]); columns.append([v for _, v in flags[3]])
    for underlying, name, comment, members in more:
        types.append(underlying); names.append(name); comments.append(comment); columns.append(members)
    height = max(len(c) for c in columns)
    rows = [[c[i] if i < len(c) else None for c in columns] for i in range(height)]
    sheet(wb, "Enums", [types, names, comments], rows)

    sheet(wb, "Game.Config", [], [
        ["int", "MaxLevel", "최대 레벨", 100],
        ["int", "MaxPartySize", "파티 최대 인원", 4],
        ["int", "InventorySlots", "기본 인벤토리 칸 수", 120],
        ["Ref<Map>", "StartMap", "캐릭터가 처음 서는 맵", 1000],
        ["Ref<Item>[]", "StarterItems", "시작 아이템", "10001,10002,30101"],
        ["float", "ExpRate", "경험치 배율", 1.0],
        ["float", "DropRate", "드롭 배율", 1.0],
        ["DateTime", "SeasonStartsAt", "시즌 시작 (UTC)", datetime.datetime(2026, 12, 1, 0, 0, 0)],
        ["bool", "PvpEnabled", "대전 허용", True],
        ["string", "Version", "데이터 버전", "1.0.0"],
    ], header_rows=0, widths=22)

    sheet(wb, "#ReadMe", [], [
        ["이 시트는 이름이 #로 시작해 빌드에서 제외됩니다."],
        ["공용 enum은 Enums 시트에, 전투 전용 enum은 Monster.xlsx의 Battle.Enums 시트에 있습니다."],
        ["행 첫 칸이 ~ 또는 #로 시작하면 그 행은 읽지 않습니다. 기획 메모용으로 쓰세요."],
    ], header_rows=0, widths=80)
    save(wb, folder, "Common.xlsx")


JOBS = [
    (100, "Warrior", "전사", "Str", "Sword,Axe", 160, 40),
    (200, "Mage", "마법사", "Int", "Staff,Wand", 90, 140),
    (300, "Archer", "궁수", "Dex", "Bow,Crossbow", 110, 70),
    (400, "Rogue", "도적", "Luk", "Dagger,Claw", 120, 60),
]
ADVANCED = [
    (110, "Knight", "기사", 100, "Str", "Sword", 220, 50),
    (120, "Berserker", "광전사", 100, "Str", "Axe", 240, 40),
    (210, "Sorcerer", "소서러", 200, "Int", "Staff", 110, 200),
    (220, "Cleric", "클레릭", 200, "Int", "Wand", 130, 180),
    (310, "Ranger", "레인저", 300, "Dex", "Bow", 140, 90),
    (320, "Marksman", "저격수", 300, "Dex", "Crossbow", 130, 90),
    (410, "Assassin", "어쌔신", 400, "Luk", "Claw", 150, 80),
    (420, "Shade", "섀도", 400, "Luk", "Dagger", 150, 80),
]


def character(folder):
    wb = book()
    columns = [
        ("Key<int>", "Id", "직업 번호"),
        ("Mapping<EJobId>", "Code", "코드에서 쓰는 이름"),
        ("string", "Name", "표시 이름"),
        ("int", "Tier", "전직 단계"),
        ("Ref<Job>?", "BaseJob", "이전 직업"),
        ("Enum<EStat>", "MainStat", "주 능력치"),
        ("EWeaponType[]", "Weapons", "착용 가능 무기"),
        ("int", "BaseHp", "기본 HP"),
        ("int", "BaseMp", "기본 MP"),
        ("#string", "Memo", "기획 메모"),
    ]
    table(wb, "Job", columns, [[j[0], j[1], j[2], 1, None, j[3], j[4], j[5], j[6], "1차 직업"] for j in JOBS])
    table(wb, "Job.Advanced", columns, [[a[0], a[1], a[2], 2, a[3], a[4], a[5], a[6], a[7], "2차 전직"] for a in ADVANCED])

    levels = []
    for level in range(1, 101):
        levels.append([level, int(15 * level ** 2.6 + 40 * level), 5 if level > 1 else 0, 3 if level % 10 == 0 else 1, 12 * level])
    table(wb, "Level", [
        ("Key<int>", "Level", "레벨"),
        ("long", "ExpToNext", "다음 레벨까지 경험치"),
        ("int", "StatPoints", "레벨업 시 능력치 포인트"),
        ("int", "SkillPoints", "레벨업 시 스킬 포인트"),
        ("int", "HpBonus", "누적 HP 보너스"),
    ], levels)

    skill_names = {
        100: [("slash", "베기", "None"), ("taunt", "도발", "None"), ("guard", "방어 태세", "None")],
        110: [("holy_strike", "성스러운 일격", "Light"), ("shield_wall", "방패 벽", "None")],
        120: [("rampage", "광란", "None"), ("ground_breaker", "대지 가르기", "Earth")],
        200: [("fire_bolt", "화염구", "Fire"), ("frost_nova", "서리 폭발", "Water"), ("mana_shield", "마나 방패", "None")],
        210: [("meteor", "운석 낙하", "Fire"), ("chain_lightning", "연쇄 번개", "Wind")],
        220: [("heal", "치유", "Light"), ("bless", "축복", "Light")],
        300: [("double_shot", "연속 사격", "None"), ("eagle_eye", "매의 눈", "None")],
        310: [("arrow_rain", "화살비", "Wind"), ("trap", "덫 설치", "Earth")],
        320: [("snipe", "저격", "None"), ("piercing", "관통 사격", "None")],
        400: [("stab", "찌르기", "None"), ("smoke", "연막", "Dark")],
        410: [("star_throw", "표창 던지기", "None"), ("venom", "맹독", "Dark")],
        420: [("shadow_step", "그림자 이동", "Dark"), ("assassinate", "암살", "Dark")],
    }
    skills = []
    skill_id = 1000
    for job, entries in skill_names.items():
        for order, (code, name, element) in enumerate(entries):
            skill_id += 1
            base = 1.2 + order * 0.6 + (0.8 if job % 100 else 0)
            damage = ",".join(f"{base + step * 0.15:.2f}" for step in range(5))
            required = (10 if job % 100 == 0 else 30) + order * 5
            skills.append([skill_id, code, name, job, element, required, 10 if order < 2 else 5,
                           round(3 + order * 4.5, 1), 8 + order * 6, damage])
    table(wb, "Skill", [
        ("Key<int>", "Id", "스킬 번호"),
        ("Key<string>", "Code", "코드"),
        ("string", "Name", "표시 이름"),
        ("Ref<Job>", "Job", "배우는 직업"),
        ("Enum<EElement>", "Element", "속성"),
        ("int", "RequiredLevel", "습득 레벨"),
        ("int", "MaxLevel", "최대 스킬 레벨"),
        ("float", "Cooldown", "재사용 대기 (초)"),
        ("int", "MpCost", "MP 소모"),
        ("float[]", "Damage", "레벨별 피해 계수"),
    ], skills)
    save(wb, folder, "Character.xlsx")


ITEM_COLUMNS = [
    ("Key<int>", "Id", "아이템 번호"),
    ("Key<string>", "Code", "코드"),
    ("string", "Name", "표시 이름"),
    ("Enum<EItemType>", "Type", "종류"),
    ("Enum<ERarity>", "Rarity", "등급"),
    ("EItemTag", "Tags", "속성"),
    ("int", "MaxStack", "최대 겹침"),
    ("int", "BuyPrice", "구매가"),
    ("int", "SellPrice", "판매가"),
    ("Index<int>", "Tier", "티어"),
    ("string", "Description", "설명"),
]

WEAPONS = [  # id, code, name, weapon type, job, level, atk, matk
    (20001, "wood_sword", "나무 검", "Sword", 100, 1, 12, 0),
    (20002, "iron_sword", "강철 검", "Sword", 100, 15, 38, 0),
    (20003, "knight_blade", "기사의 검", "Sword", 110, 35, 96, 0),
    (20004, "war_axe", "전투 도끼", "Axe", 120, 35, 108, 0),
    (20011, "oak_staff", "참나무 지팡이", "Staff", 200, 1, 3, 14),
    (20012, "crystal_staff", "수정 지팡이", "Staff", 210, 35, 8, 112),
    (20013, "light_wand", "빛의 완드", "Wand", 220, 35, 6, 98),
    (20021, "short_bow", "짧은 활", "Bow", 300, 1, 11, 0),
    (20022, "hunter_bow", "사냥꾼의 활", "Bow", 310, 35, 94, 0),
    (20023, "heavy_crossbow", "중석궁", "Crossbow", 320, 35, 102, 0),
    (20031, "dagger", "단검", "Dagger", 400, 1, 10, 0),
    (20032, "night_claw", "밤의 손톱", "Claw", 410, 35, 99, 0),
    (20033, "shade_dagger", "그림자 단검", "Dagger", 420, 35, 97, 0),
]
ARMORS = [  # id, code, name, slot, level, def
    (21001, "cloth_cap", "천 모자", "Head", 1, 3), (21002, "cloth_shirt", "천 옷", "Body", 1, 6),
    (21003, "leather_boots", "가죽 장화", "Feet", 5, 4), (21004, "leather_gloves", "가죽 장갑", "Hands", 5, 3),
    (21011, "guardian_helm", "수호자의 투구", "Head", 40, 28), (21012, "guardian_armor", "수호자의 갑옷", "Body", 40, 52),
    (21013, "guardian_greaves", "수호자의 각반", "Legs", 40, 34), (21014, "guardian_gauntlet", "수호자의 건틀릿", "Hands", 40, 22),
    (21021, "ember_ring", "불씨 반지", "Ring", 30, 2), (21022, "tide_necklace", "조류 목걸이", "Necklace", 30, 3),
]


def item(folder):
    wb = book()
    consumables = [
        [10001, "hp_potion_s", "작은 회복 물약", "Consumable", "Common", "Tradable, Stackable, Sellable", 200, 50, 12, 1, "HP를 150 회복합니다."],
        [10002, "mp_potion_s", "작은 마나 물약", "Consumable", "Common", "Tradable, Stackable, Sellable", 200, 60, 15, 1, "MP를 100 회복합니다."],
        [10003, "hp_potion_m", "회복 물약", "Consumable", "Uncommon", "Tradable, Stackable, Sellable", 200, 180, 45, 2, "HP를 600 회복합니다."],
        [10004, "mp_potion_m", "마나 물약", "Consumable", "Uncommon", "Tradable, Stackable, Sellable", 200, 210, 52, 2, "MP를 400 회복합니다."],
        [10005, "elixir", "엘릭서", "Consumable", "Rare", "Stackable, Sellable", 50, 0, 900, 3, "HP와 MP를 모두 회복합니다."],
        [10006, "return_scroll", "귀환 주문서", "Consumable", "Common", "Tradable, Stackable, Sellable", 100, 300, 75, 1, "가장 가까운 마을로 돌아갑니다."],
        [10007, "exp_charm", "경험의 부적", "Consumable", "Epic", "Bound, Event", 1, 0, 0, 3, "30분간 경험치 2배."],
        ["~10008", "draft_potion", "미완성 물약", "Consumable", "Common", "", 1, 0, 0, 1, "아직 기획 중 (빌드 제외)"],
        [10101, "starter_box", "모험가 상자", "Box", "Uncommon", "Bound", 1, 0, 0, 1, "열면 초보자용 장비가 나옵니다."],
        [10102, "boss_box", "보스 전리품 상자", "Box", "Epic", "Tradable", 1, 0, 2500, 3, "보스가 남긴 상자."],
        [10201, "lost_letter", "잃어버린 편지", "Quest", "Common", "Bound", 1, 0, 0, 1, "누군가에게 전해야 할 편지."],
    ]
    table(wb, "Item", ITEM_COLUMNS, consumables)

    materials = []
    for index, (code, name, rarity, tier) in enumerate([
        ("iron_ore", "철광석", "Common", 1), ("silver_ore", "은광석", "Uncommon", 2), ("mithril_ore", "미스릴 광석", "Rare", 3),
        ("soft_hide", "부드러운 가죽", "Common", 1), ("thick_hide", "질긴 가죽", "Uncommon", 2), ("fire_core", "불꽃의 핵", "Rare", 3),
        ("tide_pearl", "조류의 진주", "Rare", 3), ("shadow_dust", "그림자 가루", "Epic", 4), ("dragon_scale", "용의 비늘", "Legendary", 5),
        ("upgrade_stone", "강화석", "Uncommon", 2), ("blessed_stone", "축복받은 강화석", "Epic", 4),
    ]):
        price = [8, 30, 120, 450, 2000][tier - 1]
        materials.append([30101 + index, code, name, "Material", rarity, "Tradable, Stackable, Sellable", 999, price * 3, price, tier, ""])
    table(wb, "Item.Material", ITEM_COLUMNS, materials)

    equipment_items = []
    for (iid, code, name, wtype, job, level, atk, matk) in WEAPONS:
        rarity = "Common" if level == 1 else "Uncommon" if level < 30 else "Rare"
        equipment_items.append([iid, code, name, "Equipment", rarity, "Tradable, Sellable", 1, level * 40, level * 10, 1 + level // 20, ""])
    for (iid, code, name, slot, level, defense) in ARMORS:
        rarity = "Common" if level < 10 else "Rare" if level < 40 else "Epic"
        equipment_items.append([iid, code, name, "Equipment", rarity, "Tradable, Sellable", 1, level * 35, level * 9, 1 + level // 20, ""])
    table(wb, "Item.Equipment", ITEM_COLUMNS, equipment_items)

    equipment_columns = [
        ("Key<int>", "Id", "아이템 번호와 같음"),
        ("Ref<Item>", "Item", "아이템"),
        ("Enum<EEquipSlot>", "Slot", "장착 부위"),
        ("Enum<EWeaponType>", "WeaponType", "무기 종류"),
        ("Ref<Job>?", "Job", "착용 직업 (비우면 전 직업)"),
        ("int", "RequiredLevel", "착용 레벨"),
        ("int", "Atk", "공격력"),
        ("int", "MAtk", "마법 공격력"),
        ("int", "Def", "방어력"),
        ("EStat[]", "BonusStats", "추가 능력치"),
        ("int[]", "BonusValues", "추가 능력치 값"),
    ]
    equipment = []
    for (iid, code, name, wtype, job, level, atk, matk) in WEAPONS:
        main = {"Sword": "Str", "Axe": "Str", "Staff": "Int", "Wand": "Int", "Bow": "Dex", "Crossbow": "Dex", "Dagger": "Luk", "Claw": "Luk"}[wtype]
        bonus = max(1, level // 5)
        equipment.append([iid, iid, "Weapon", wtype, job, level, atk, matk, 0, main if level > 1 else None, str(bonus) if level > 1 else None])
    for (iid, code, name, slot, level, defense) in ARMORS:
        stats = "MaxHp,Def" if slot in ("Body", "Legs") else "Crit" if slot == "Ring" else "MaxMp" if slot == "Necklace" else None
        values = f"{level * 6},{level // 2}" if slot in ("Body", "Legs") else str(max(1, level // 10)) if stats else None
        equipment.append([iid, iid, slot, "None", None, level, 0, 0, defense, stats, values])
    table(wb, "Equipment", equipment_columns, equipment)

    # Added columns for some of the equipment only; the others get empty cells.
    upgrade_rows = []
    for (iid, _, _, _, _, level, _, _) in WEAPONS:
        if level >= 15:
            upgrade_rows.append([iid, 10 if level < 35 else 15, level * 120, 30110 if level < 35 else 30111])
    for (iid, _, _, _, level, _) in ARMORS:
        if level >= 40:
            upgrade_rows.append([iid, 12, level * 100, 30111])
    table(wb, "Equipment.Col.Upgrade", [
        ("Key<int>", "Id", "장비 번호"),
        ("int", "MaxUpgrade", "최대 강화 횟수"),
        ("int", "UpgradeGold", "강화 비용"),
        ("Ref<Item>?", "UpgradeStone", "강화 재료"),
    ], upgrade_rows)

    table(wb, "SetBonus", [
        ("Key<int>", "Id", "세트 번호"),
        ("string", "Name", "세트 이름"),
        ("Ref<Equipment>[]", "Pieces", "세트 구성"),
        ("int", "RequiredPieces", "효과 발동 개수"),
        ("EStat[]", "Stats", "세트 효과 능력치"),
        ("int[]", "Values", "세트 효과 값"),
    ], [
        [1, "초보 모험가 세트", "21001,21002,21003,21004", 3, "MaxHp,Def", "100,10"],
        [2, "수호자 세트", "21011,21012,21013,21014", 4, "MaxHp,Def,MDef", "1200,80,60"],
        [3, "원소 장신구", "21021,21022", 2, "MAtk,Crit", "40,5"],
    ])

    enhance = []
    for level in range(1, 16):
        success = max(0.3, 1.0 - 0.05 * (level - 1)) if level <= 10 else max(0.05, 0.3 - 0.05 * (level - 10))
        destroy = 0.0 if level <= 10 else round(0.02 * (level - 10), 2)
        enhance.append([level, round(success, 2), destroy, 500 * level * level, 30110 if level <= 10 else 30111, 1 + level // 3])
    table(wb, "Enhance", [
        ("Key<int>", "Level", "강화 단계"),
        ("float", "SuccessRate", "성공 확률"),
        ("float", "DestroyRate", "파괴 확률"),
        ("int", "Gold", "골드 비용"),
        ("Ref<Item>", "Stone", "필요 강화석"),
        ("int", "StoneCount", "필요 개수"),
    ], enhance)
    save(wb, folder, "Item.xlsx")


REGIONS = [
    (1, "Elderwood", "엘더우드", 1),
    (2, "AshenWastes", "잿빛 황야", 20),
    (3, "Frostmere", "서리호수", 40),
    (4, "SunkenRuins", "가라앉은 유적", 60),
]
MAPS = [  # id, code, name, region, type, min, max, element, bgm
    (1000, "elder_village", "엘더 마을", 1, "Town", 1, 100, "None", "bgm_village"),
    (1001, "green_meadow", "초록 들판", 1, "Field", 1, 10, "None", "bgm_meadow"),
    (1002, "whisper_forest", "속삭이는 숲", 1, "Field", 8, 20, "Earth", "bgm_forest"),
    (2000, "cinder_camp", "잿불 야영지", 2, "Town", 1, 100, "None", "bgm_camp"),
    (2001, "scorched_plain", "그을린 평원", 2, "Field", 20, 32, "Fire", "bgm_desert"),
    (2002, "magma_rift", "용암 균열", 2, "Field", 30, 42, "Fire", "bgm_desert"),
    (3000, "frost_harbor", "서리 항구", 3, "Town", 1, 100, "None", "bgm_harbor"),
    (3001, "ice_shelf", "빙붕", 3, "Field", 40, 52, "Water", "bgm_snow"),
    (4000, "ruin_gate", "유적 입구", 4, "Town", 1, 100, "None", "bgm_ruins"),
    (4001, "drowned_halls", "잠긴 회랑", 4, "Field", 60, 75, "Dark", "bgm_ruins"),
]
DUNGEONS = [
    (1101, "goblin_den", "고블린 소굴", 1, "Dungeon", 12, 20, "Earth", "bgm_dungeon"),
    (2101, "ember_core", "불씨 심장부", 2, "Dungeon", 35, 45, "Fire", "bgm_boss"),
    (3101, "glacier_vault", "빙하 금고", 3, "Dungeon", 50, 60, "Water", "bgm_boss"),
    (4101, "abyss_throne", "심연의 왕좌", 4, "Raid", 75, 100, "Dark", "bgm_raid"),
]


def world(folder):
    wb = book()
    table(wb, "Region", [
        ("Key<int>", "Id", "지역 번호"),
        ("Mapping<ERegionId>", "Code", "코드에서 쓰는 이름"),
        ("string", "Name", "표시 이름"),
        ("int", "RecommendedLevel", "권장 레벨"),
    ], [list(r) for r in REGIONS])

    map_columns = [
        ("Key<int>", "Id", "맵 번호"),
        ("Key<string>", "Code", "코드"),
        ("string", "Name", "표시 이름"),
        ("Ref<Region>", "Region", "지역"),
        ("Enum<EMapType>", "Type", "종류"),
        ("int", "MinLevel", "입장 최소 레벨"),
        ("int", "MaxLevel", "권장 최대 레벨"),
        ("Enum<EElement>", "Element", "지형 속성"),
        ("string", "Bgm", "배경음"),
    ]
    table(wb, "Map", map_columns, [list(m) for m in MAPS])
    table(wb, "Map.Dungeon", map_columns, [list(d) for d in DUNGEONS])

    table(wb, "Portal", [
        ("Key<int>", "Id", "포탈 번호"),
        ("Ref<Map>", "From", "출발 맵"),
        ("Ref<Map>", "To", "도착 맵"),
        ("float", "X", "위치 X"),
        ("float", "Y", "위치 Y"),
    ], [
        [1, 1000, 1001, 120.5, 0], [2, 1001, 1002, 840, 0], [3, 1002, 1101, 1220, 32], [4, 1000, 2000, -40, 0],
        [5, 2000, 2001, 300, 0], [6, 2001, 2002, 990, 0], [7, 2002, 2101, 1400, 64], [8, 2000, 3000, -40, 0],
        [9, 3000, 3001, 410, 0], [10, 3001, 3101, 1310, 16], [11, 3000, 4000, -40, 0], [12, 4000, 4001, 260, 0],
        [13, 4001, 4101, 1650, 96],
    ])

    table(wb, "Npc", [
        ("Key<int>", "Id", "NPC 번호"),
        ("string", "Name", "이름"),
        ("Ref<Map>", "Map", "있는 맵"),
        ("Ref<Shop>?", "Shop", "상점 (없으면 비움)"),
        ("string", "Greeting", "인사말"),
    ], [
        [1, "촌장 오린", 1000, None, "엘더 마을에 온 걸 환영하네."],
        [2, "상인 미라", 1000, 1, "필요한 건 다 있어요!"],
        [3, "대장장이 브론", 1000, 2, "장비는 늘 손질해 두라고."],
        [4, "정찰병 케이", 2000, None, "황야는 생각보다 위험해."],
        [5, "항구지기 세라", 3000, 3, "배는 바람이 정해요."],
        [6, "학자 이든", 4000, None, "이 유적엔 아직 밝혀지지 않은 게 많지."],
    ])
    save(wb, folder, "World.xlsx")


MONSTERS = [  # id, code, name, rank, level, ai, element, map
    (1, "GreenSlime", "초록 슬라임", "Normal", 2, "Passive", "None"),
    (2, "Mushroom", "버섯돌이", "Normal", 5, "Passive", "Earth"),
    (3, "Wolf", "들늑대", "Normal", 9, "Aggressive", "None"),
    (4, "Goblin", "고블린", "Normal", 14, "Aggressive", "Earth"),
    (5, "GoblinShaman", "고블린 주술사", "Elite", 18, "Caster", "Earth"),
    (6, "FireImp", "불꽃 임프", "Normal", 22, "Caster", "Fire"),
    (7, "Scorpion", "모래 전갈", "Normal", 26, "Aggressive", "Earth"),
    (8, "LavaGolem", "용암 골렘", "Elite", 38, "Aggressive", "Fire"),
    (9, "FrostWisp", "서리 정령", "Normal", 44, "Ranged", "Water"),
    (10, "IceTroll", "얼음 트롤", "Elite", 50, "Aggressive", "Water"),
    (11, "DrownedKnight", "익사한 기사", "Normal", 64, "Aggressive", "Dark"),
    (12, "Wraith", "망령", "Elite", 70, "Caster", "Dark"),
]
BOSSES = [
    (101, "GoblinChief", "고블린 족장", "Boss", 20, "Aggressive", "Earth"),
    (102, "EmberDrake", "불씨 드레이크", "Boss", 45, "Ranged", "Fire"),
    (103, "GlacierQueen", "빙하 여왕", "Boss", 60, "Caster", "Water"),
    (104, "AbyssLord", "심연의 군주", "RaidBoss", 90, "Caster", "Dark"),
]


def monster(folder):
    wb = book()
    sheet(wb, "Battle.Enums", [["byte"], ["EAiType"], ["몬스터 행동 방식"]],
          [["Passive"], ["Aggressive"], ["Ranged"], ["Caster"]])

    columns = [
        ("Key<int>", "Id", "몬스터 번호"),
        ("Mapping<EMonsterId>", "Code", "코드에서 쓰는 이름"),
        ("string", "Name", "표시 이름"),
        ("Enum<EMonsterRank>", "Rank", "등급"),
        ("int", "Level", "레벨"),
        ("long", "Hp", "HP"),
        ("int", "Atk", "공격력"),
        ("int", "Def", "방어력"),
        ("Enum<EAiType>", "Ai", "행동 방식"),
        ("Enum<EElement>", "Element", "속성"),
        ("long", "Exp", "처치 경험치"),
        ("Ref<Drop>", "Drop", "드롭 테이블"),
    ]

    def rows(entries):
        out = []
        for (mid, code, name, rank, level, ai, element) in entries:
            mult = {"Normal": 1, "Elite": 4, "Boss": 30, "RaidBoss": 400}[rank]
            hp = int((60 + level ** 2.2 * 8) * mult)
            drop = 9 if rank == "RaidBoss" else 8 if rank == "Boss" else 1 + (level // 20) * 2 + (1 if rank == "Elite" else 0)
            out.append([mid, code, name, rank, level, hp, int(8 + level * 3.2 * (1 + 0.3 * (mult > 1))),
                        int(2 + level * 1.4), ai, element, int(hp * 0.12), drop])
        return out

    table(wb, "Monster", columns, rows(MONSTERS))
    table(wb, "Monster.Boss", columns, rows(BOSSES))

    spawn_plan = [(1001, 1, 12), (1001, 2, 8), (1002, 3, 10), (1002, 2, 6), (1101, 4, 14), (1101, 5, 3), (1101, 101, 1),
                  (2001, 6, 12), (2001, 7, 10), (2002, 8, 4), (2002, 6, 8), (2101, 8, 6), (2101, 102, 1),
                  (3001, 9, 12), (3001, 10, 3), (3101, 10, 6), (3101, 103, 1), (4001, 11, 14), (4001, 12, 4), (4101, 104, 1)]
    table(wb, "Spawn", [
        ("Key<int>", "Id", "배치 번호"),
        ("Ref<Map>", "Map", "맵"),
        ("Ref<Monster>", "Monster", "몬스터"),
        ("int", "Count", "동시 출현 수"),
        ("float", "RespawnSeconds", "재출현 (초)"),
    ], [[index + 1, m, mon, count, 0 if mon >= 100 else round(8 + count * 0.5, 1)] for index, (m, mon, count) in enumerate(spawn_plan)])

    table(wb, "Drop", [
        ("Key<int>", "Id", "드롭 테이블 번호"),
        ("string", "Name", "이름"),
        ("Ref<Item>[]", "Items", "아이템"),
        ("float[]", "Chances", "확률 (아이템과 같은 순서)"),
        ("int", "GoldMin", "최소 골드"),
        ("int", "GoldMax", "최대 골드"),
    ], [
        [1, "들판 일반", "10001,30101,30104", "0.08,0.12,0.10", 3, 12],
        [2, "들판 정예", "10003,30102,20002", "0.15,0.20,0.01", 20, 60],
        [3, "황야 일반", "10003,30102,30106", "0.10,0.15,0.05", 25, 70],
        [4, "황야 정예", "10004,30106,20003,20004", "0.20,0.25,0.005,0.005", 120, 300],
        [5, "서리 일반", "10003,30103,30107", "0.12,0.10,0.06", 60, 140],
        [6, "서리 정예", "10005,30107,21021,21022", "0.05,0.25,0.01,0.01", 250, 600],
        [7, "유적 일반", "10005,30108", "0.08,0.12", 150, 320],
        [8, "보스 공통", "10102,30111,21012", "1.0,0.5,0.05", 3000, 8000],
        [9, "레이드 보스", "10102,30109,30111,21012,21011", "1.0,0.3,1.0,0.2,0.2", 50000, 120000],
    ])
    save(wb, folder, "Monster.xlsx")


def quest(folder):
    wb = book()
    columns = [
        ("Key<int>", "Id", "퀘스트 번호"),
        ("string", "Name", "제목"),
        ("Enum<EQuestType>", "Type", "종류"),
        ("int", "RequiredLevel", "수락 레벨"),
        ("Ref<Quest>?", "Previous", "선행 퀘스트"),
        ("Ref<Npc>", "Giver", "주는 NPC"),
        ("long", "RewardExp", "보상 경험치"),
        ("int", "RewardGold", "보상 골드"),
        ("Ref<Item>[]", "RewardItems", "보상 아이템"),
        ("int[]", "RewardCounts", "보상 개수"),
    ]
    main = [
        [1, "새로운 시작", "Main", 1, None, 1, 100, 50, "10101", "1"],
        [2, "들판의 소란", "Main", 3, 1, 1, 400, 120, "10001", "10"],
        [3, "숲의 속삭임", "Main", 9, 2, 1, 1500, 300, "20002", "1"],
        [4, "고블린 소굴", "Main", 14, 3, 1, 5200, 800, "10003", "10"],
        [5, "족장의 최후", "Main", 18, 4, 1, 12000, 2000, "10102", "1"],
        [6, "잿빛 황야로", "Main", 20, 5, 4, 20000, 2500, "10006", "5"],
        [7, "불씨의 심장", "Main", 35, 6, 4, 90000, 9000, "30106", "5"],
        [8, "서리 항구의 의뢰", "Main", 40, 7, 5, 150000, 12000, "10005", "3"],
        [9, "빙하 금고", "Main", 50, 8, 5, 320000, 20000, "30107,30111", "3,5"],
        [10, "심연으로", "Main", 60, 9, 6, 800000, 50000, "30108", "10"],
        [21, "편지 배달", "Side", 5, None, 1, 300, 200, "10001", "5"],
        [22, "대장장이의 부탁", "Side", 12, None, 3, 2000, 600, "30101,30104", "10,10"],
        [23, "항구의 진주", "Side", 42, None, 5, 120000, 8000, "30107", "2"],
    ]
    table(wb, "Quest", columns, main)
    table(wb, "Quest.Daily", columns, [
        [101, "[일일] 슬라임 퇴치", "Daily", 1, None, 1, 200, 100, "10001", "3"],
        [102, "[일일] 황야 정찰", "Daily", 20, None, 4, 8000, 1500, "10003", "5"],
        [103, "[일일] 서리 채집", "Daily", 40, None, 5, 40000, 4000, "30107", "1"],
        [201, "[주간] 심연의 군주", "Weekly", 75, None, 6, 2000000, 100000, "10102", "3"],
    ])

    objectives = [
        [1, 1, "Talk", 1, 1, "촌장과 이야기하기"],
        [2, 2, "Kill", 1, 10, "초록 슬라임 10마리"],
        [3, 2, "Kill", 2, 5, "버섯돌이 5마리"],
        [4, 3, "Reach", 1002, 1, "속삭이는 숲 도착"],
        [5, 3, "Kill", 3, 8, "들늑대 8마리"],
        [6, 4, "Kill", 4, 15, "고블린 15마리"],
        [7, 4, "Collect", 30104, 10, "부드러운 가죽 10개"],
        [8, 5, "Kill", 101, 1, "고블린 족장 처치"],
        [9, 6, "Reach", 2000, 1, "잿불 야영지 도착"],
        [10, 7, "Kill", 102, 1, "불씨 드레이크 처치"],
        [11, 8, "Talk", 5, 1, "항구지기와 이야기하기"],
        [12, 9, "Kill", 103, 1, "빙하 여왕 처치"],
        [13, 10, "Reach", 4001, 1, "잠긴 회랑 도착"],
        [14, 21, "Talk", 2, 1, "상인에게 편지 전달"],
        [15, 22, "Collect", 30101, 10, "철광석 10개"],
        [16, 23, "Collect", 30107, 3, "조류의 진주 3개"],
        [17, 101, "Kill", 1, 30, "초록 슬라임 30마리"],
        [18, 102, "Kill", 7, 20, "모래 전갈 20마리"],
        [19, 103, "Collect", 30103, 5, "미스릴 광석 5개"],
        [20, 201, "Kill", 104, 1, "심연의 군주 처치"],
    ]
    table(wb, "QuestObjective", [
        ("Key<int>", "Id", "목표 번호"),
        ("Index<int>", "QuestId", "퀘스트 번호"),
        ("Enum<EObjective>", "Type", "목표 종류"),
        ("int", "TargetId", "대상 번호 (몬스터, 아이템, 맵, NPC)"),
        ("int", "Count", "필요 수"),
        ("string", "Hint", "안내 문구"),
    ], objectives)
    save(wb, folder, "Quest.xlsx")


def shop(folder):
    wb = book()
    table(wb, "Shop", [
        ("Key<int>", "Id", "상점 번호"),
        ("string", "Name", "이름"),
        ("Enum<ECurrency>", "Currency", "결제 재화"),
        ("Ref<Map>", "Map", "위치"),
    ], [[1, "미라의 잡화점", "Gold", 1000], [2, "브론의 대장간", "Gold", 1000], [3, "항구 교역소", "Gold", 3000], [4, "명예 상점", "Honor", 4000]])

    goods = [
        (1, 10001, 50, 0), (1, 10002, 60, 0), (1, 10003, 180, 0), (1, 10004, 210, 0), (1, 10006, 300, 0),
        (2, 20001, 40, 0), (2, 20011, 40, 0), (2, 20021, 40, 0), (2, 20031, 40, 0), (2, 20002, 600, 0), (2, 30110, 900, 10),
        (3, 10005, 2400, 5), (3, 30107, 1500, 10), (3, 21022, 18000, 1),
        (4, 21011, 1200, 1), (4, 21012, 2000, 1), (4, 30111, 300, 5),
    ]
    table(wb, "ShopGoods", [
        ("Key<int>", "Id", "판매 번호"),
        ("Index<int>", "ShopId", "상점 번호"),
        ("Ref<Item>", "Item", "아이템"),
        ("int", "Price", "가격"),
        ("int", "DailyLimit", "일일 구매 제한 (0은 무제한)"),
    ], [[index + 1, s, i, p, limit] for index, (s, i, p, limit) in enumerate(goods)])

    table(wb, "Gacha", [
        ("Key<int>", "Id", "뽑기 번호"),
        ("string", "Name", "이름"),
        ("Enum<ECurrency>", "Currency", "재화"),
        ("int", "Cost", "1회 비용"),
        ("Ref<Item>[]", "Pool", "등장 아이템"),
        ("float[]", "Weights", "가중치"),
        ("DateTime", "EndsAt", "종료 시각 (UTC)"),
    ], [
        [1, "모험가 지원 상자", "Gem", 100, "10003,10004,10005,30110", "40,40,15,5", datetime.datetime(2027, 1, 31, 23, 59, 59)],
        [2, "전설의 대장간", "Gem", 300, "20003,20004,20012,20022,30111,30109", "15,15,15,15,38,2", datetime.datetime(2027, 2, 28, 23, 59, 59)],
    ])
    save(wb, folder, "Shop.xlsx")


def localization(folder):
    path = os.path.join(folder, "Localization")
    os.makedirs(path, exist_ok=True)
    rows = [
        ("Key<string>", "string", "string", "#string"), ("Id", "Ko", "En", "Note"), ("문구 번호", "한국어", "영어", "메모"),
        ("ui.start", "게임 시작", "Start", ""), ("ui.continue", "이어하기", "Continue", ""), ("ui.settings", "설정", "Settings", ""),
        ("ui.quit", "종료", "Quit", ""), ("ui.inventory", "인벤토리", "Inventory", ""), ("ui.skill", "스킬", "Skills", ""),
        ("ui.quest", "퀘스트", "Quests", ""), ("ui.party", "파티", "Party", ""), ("ui.shop", "상점", "Shop", ""),
        ("ui.level_up", "레벨 업!", "Level up!", "레벨 업 연출"), ("ui.gold", "골드", "Gold", ""),
        ("msg.not_enough_gold", "골드가 부족합니다.", "Not enough gold.", ""),
        ("msg.inventory_full", "인벤토리가 가득 찼습니다.", "Your inventory is full.", ""),
        ("msg.enhance_success", "강화에 성공했습니다!", "Enhancement succeeded!", ""),
        ("msg.enhance_fail", "강화에 실패했습니다.", "Enhancement failed.", ""),
        ("msg.welcome", "\"엘더 마을\"에 오신 것을 환영합니다,\n모험가님.", "Welcome to \"Elder Village\",\nadventurer.", "따옴표, 쉼표, 줄바꿈 예시"),
    ]
    with open(os.path.join(path, "Text.csv"), "w", newline="", encoding="utf-8") as file:
        writer = csv.writer(file, lineterminator="\n")
        writer.writerows(rows)
    print("wrote", os.path.join(path, "Text.csv"))


def main(folder):
    os.makedirs(folder, exist_ok=True)
    common(folder)
    character(folder)
    item(folder)
    world(folder)
    monster(folder)
    quest(folder)
    shop(folder)
    localization(folder)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Sheets")
