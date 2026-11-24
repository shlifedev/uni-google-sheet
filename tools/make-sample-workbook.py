#!/usr/bin/env python3
import datetime
import sys

from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill

HEADER_FILL = PatternFill("solid", fgColor="DDEBF7")
BOLD = Font(bold=True)


def add_sheet(workbook, name, rows, header_rows=3):
    sheet = workbook.create_sheet(name)
    for row in rows:
        sheet.append(row)
    for row_index in range(1, min(header_rows, sheet.max_row) + 1):
        for cell in sheet[row_index]:
            cell.fill = HEADER_FILL
            cell.font = BOLD
    for column in sheet.columns:
        sheet.column_dimensions[column[0].column_letter].width = 16
    return sheet


def main(path):
    workbook = Workbook()
    workbook.remove(workbook.active)

    add_sheet(workbook, "Enums", [
        ["byte", None, "Flags<int>", None, None],
        ["EItemKind", None, "EItemTag", None, "ERarity"],
        ["Kind of item", None, "Tags an item can carry", None, "How rare an item is"],
        ["None", None, "None", 0, "Common"],
        ["Weapon", None, "Tradable", "1 << 0", "Rare"],
        ["Armor", None, "Stackable", "1 << 1", "Epic"],
        ["Consumable", None, "Quest", "1 << 2", None],
        ["Material", 10, "Sellable", "Tradable | Stackable", None],
    ])

    released = datetime.datetime(2026, 1, 31, 13, 30, 0)
    add_sheet(workbook, "Item", [
        ["Key<int>", "Key<string>", "string", "Enum<EItemKind>", "EItemTag", "Index<int>", "int", "float", "double", "bool", "DateTime", "float[]", "string[]", "long", "#memo", "int", "int", "Enum<ERarity>"],
        ["Id", "Code", "Name", "Kind", "Tags", "Tier", "Price", "Weight", "DropRate", "Tradable", "ReleasedAt", "Stats", "Aliases", "Exp", "Memo", "Sockets", "Sockets", "Rarity"],
        ["Item id", "Name used in code", "Display name", "Kind of item", "Tags", "Tier, for grouping", "Price in gold", "Weight in kg", "Chance to drop", "Can be traded", "Release date (UTC)", "Stat values", "Other names", "Experience", "Designer notes", "Socket sizes", "Socket sizes", "Rarity"],
        [1001, "wooden_sword", "Wooden Sword", "Weapon", "Tradable", 1, 100, 2.5, 0.25, True, datetime.date(2026, 1, 31), "3,1.5", "sword,blade", 10, "starter weapon", 1, 2, "Common"],
        [1002, "leather_armor", "Leather Armor", "Armor", "Tradable", 1, 150, 4, 0.1, True, released, "5", None, 15, None, 3, None, None],
        [2001, "potion", "포션 Potion", "Consumable", "Tradable, Stackable", 1, 25, 0.5, 0.5, True, released, None, "[heal, hp]", 0, "unicode name", None, None, "Common"],
        ["~2002", "draft", "Not ready", "Consumable", None, 1, 0, 0, 0, False, None, None, None, 0, "this row is skipped", None, None, None],
        [None] * 18,
        [3001, "iron_ore", 'Ore "raw", heavy\nsecond line', "Material", "Sellable", 2, 7, 12.25, 0.75, False, None, "1,2,3", None, 9007199254740991, "quotes, comma and a line break", None, None, "Rare"],
    ])

    add_sheet(workbook, "Item.Rare", [
        ["Key<string>", "Key<int>", "string", "Enum<EItemKind>", "EItemTag", "Index<int>", "int", "float", "double", "bool", "DateTime", "float[]", "string[]", "long", "int", "int", "Enum<ERarity>"],
        ["Code", "Id", "Name", "Kind", "Tags", "Tier", "Price", "Weight", "DropRate", "Tradable", "ReleasedAt", "Stats", "Aliases", "Exp", "Sockets", "Sockets", "Rarity"],
        ["Name used in code", "Item id", "Display name", "Kind of item", "Tags", "Tier", "Price", "Weight", "Chance", "Tradable", "Released", "Stats", "Aliases", "Exp", "Sockets", "Sockets", "Rarity"],
        ["dragon_blade", 9001, "Dragon Blade", "Weapon", "Quest", 3, 99999, 7.75, 0.001, False, datetime.datetime(2026, 12, 25, 0, 0, 0), "120,45.5", "legend", 5000, 4, 4, "Epic"],
        ["phoenix_mail", 9002, "Phoenix Mail", "Armor", None, 3, 88888, 9, 0.002, False, None, "80", None, 4000, None, None, "Epic"],
    ])

    add_sheet(workbook, "Item.Col.Balance", [
        ["Key<int>", "int", "int"],
        ["Id", "Attack", "Defense"],
        ["Item id", "Attack bonus", "Defense bonus"],
        [1001, 12, 0],
        [1002, 0, 8],
        [9001, 250, 10],
    ])

    add_sheet(workbook, "Monster", [
        ["Key<int>", "Mapping<EMonsterId>", "string", "int", "Ref<Item>[]", "Ref<Item>?", "Ref<Item>", "Enum<EItemKind>[]", "Index<string>"],
        ["Id", "Code", "Name", "Hp", "Drops", "Rare", "Weapon", "Weak", "Zone"],
        ["Monster id", "Name in code", "Display name", "Hit points", "Items it can drop", "Rare drop, if any", "Weapon it carries", "Kinds of item it is weak to", "Where it lives"],
        [1, "Slime", "Slime", 30, "2001", None, 1001, None, "meadow"],
        [2, "Goblin", "Goblin", 80, "1001,2001", 1002, 1001, "Weapon", "meadow"],
        [3, "Dragon", "Dragon", 5000, "9001,9002,3001", 9001, 9001, "Weapon,Consumable", "volcano"],
    ])

    add_sheet(workbook, "Text", [
        ["Key<string>", "string", "string"],
        ["Id", "Ko", "En"],
        ["Text id", "Korean", "English"],
        ["ui.start", "시작", "Start"],
        ["ui.quit", "종료", "Quit"],
        ["ui.empty", None, None],
    ])

    add_sheet(workbook, "Game.Config", [
        ["int", "MaxLevel", "Highest level a character can reach", 60],
        ["float", "GoldRate", "Multiplier on gold drops", 1.5],
        ["string", "Title", "Name shown on the title screen", "HolySheet Sample"],
        ["Ref<Item>", "StartItem", "Item every character starts with", 1001],
        ["DateTime", "OpensAt", "When the service opens (UTC)", datetime.datetime(2026, 3, 1, 9, 0, 0)],
        ["bool", "Maintenance", "Reject logins", False],
    ], header_rows=0)

    add_sheet(workbook, "#Notes", [
        ["This sheet is ignored because its name starts with #."],
        ["Use it for instructions to the people who edit the workbook."],
    ], header_rows=0)

    workbook.save(path)
    print("wrote", path)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Game.xlsx")
