#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UGS.Editor
{
    /// <summary>The language of the UGS window.</summary>
    public enum UgsLanguage
    {
        /// <summary>Korean when the operating system is in Korean, otherwise English.</summary>
        System,
        English,
        Korean
    }

    /// <summary>
    /// The window's text in English or Korean. English is written in the code and doubles as the lookup key; Korean comes
    /// from the table below, and a string missing from it shows in English. Messages from the engine (problems, hints)
    /// stay in English.
    /// <para>
    /// The choice is personal, not part of the project: it is kept in UserSettings/UgsEditor.txt, the folder Unity keeps
    /// for per-user settings and that version control ignores.
    /// </para>
    /// </summary>
    public static class UgsText
    {
        private const string SettingsFileName = "UgsEditor.txt";
        private static UgsLanguage? _language;

        /// <summary>Forces a language without saving it, for tests and screenshots. Null uses <see cref="Language"/>.</summary>
        public static UgsLanguage? Override { get; set; }

        /// <summary>The chosen language, saved for this user and project.</summary>
        public static UgsLanguage Language
        {
            get
            {
                if (_language == null)
                {
                    _language = Load();
                }

                return _language.Value;
            }
            set
            {
                _language = value;
                Save(value);
            }
        }

        /// <summary>The language text is shown in: <see cref="Language"/>, with System resolved.</summary>
        public static UgsLanguage Effective
        {
            get
            {
                UgsLanguage language = Override ?? Language;
                if (language != UgsLanguage.System)
                {
                    return language;
                }

                return Application.systemLanguage == SystemLanguage.Korean ? UgsLanguage.Korean : UgsLanguage.English;
            }
        }

        public static bool IsKorean => Effective == UgsLanguage.Korean;

        /// <summary>The text in the current language.</summary>
        public static string T(string english)
        {
            return IsKorean && Korean.TryGetValue(english, out string? korean) ? korean : english;
        }

        /// <summary>A format string in the current language, filled in.</summary>
        public static string F(string englishFormat, params object[] args)
        {
            return string.Format(T(englishFormat), args);
        }

        /// <summary>"21 tables" / "테이블 21개".</summary>
        public static string Count(int count, string noun)
        {
            if (IsKorean && KoreanCounts.TryGetValue(noun, out string? format))
            {
                return string.Format(format, count.ToString("N0"));
            }

            return count.ToString("N0") + " " + noun + (count == 1 ? "" : "s");
        }

        private static string SettingsPath => Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "UserSettings", SettingsFileName);

        private static UgsLanguage Load()
        {
            return Enum.TryParse(GetSetting("language"), true, out UgsLanguage language) ? language : UgsLanguage.System;
        }

        private static void Save(UgsLanguage language)
        {
            SetSetting("language", language.ToString());
        }

        private static Dictionary<string, string>? _settings;

        /// <summary>
        /// A personal setting of the window (the language, how the data view looks), from UserSettings/UgsEditor.txt.
        /// Empty when it was never set.
        /// </summary>
        public static string GetSetting(string key)
        {
            _settings ??= ReadSettings();
            return _settings.TryGetValue(key, out string? value) ? value : "";
        }

        /// <summary>Saves a personal setting; the other settings in the file are kept.</summary>
        public static void SetSetting(string key, string value)
        {
            _settings ??= ReadSettings();
            _settings[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var text = new System.Text.StringBuilder();
                foreach (KeyValuePair<string, string> setting in _settings)
                {
                    text.Append(setting.Key).Append('=').Append(setting.Value).Append('\n');
                }

                File.WriteAllText(SettingsPath, text.ToString());
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                Debug.LogWarning("[UGS] The setting '" + key + "' could not be saved: " + exception.Message);
            }
        }

        private static Dictionary<string, string> ReadSettings()
        {
            var settings = new Dictionary<string, string>();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (string line in File.ReadAllLines(SettingsPath))
                    {
                        string[] parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length == 2 && parts[0].Trim().Length > 0)
                        {
                            settings[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
            }

            return settings;
        }

        private static readonly Dictionary<string, string> KoreanCounts = new Dictionary<string, string>
        {
            ["table"] = "테이블 {0}개",
            ["row"] = "{0}행",
            ["column"] = "{0}열",
            ["sheet"] = "시트 {0}개",
            ["workbook"] = "워크북 {0}개",
            ["error"] = "오류 {0}개",
            ["warning"] = "경고 {0}개",
            ["enum"] = "enum {0}개",
            ["member"] = "멤버 {0}개",
            ["file"] = "파일 {0}개",
        };

        private static readonly Dictionary<string, string> Korean = new Dictionary<string, string>
        {
            // Pages and toolbar
            ["Tables"] = "테이블",
            ["Problems"] = "문제",
            ["Settings"] = "설정",
            ["Offline"] = "오프라인",
            ["Build Google sources from the saved snapshot instead of the network."] = "구글 소스를 네트워크 대신 저장된 스냅샷에서 읽습니다.",
            ["Auto build"] = "자동 빌드",
            ["Build whenever a local sheet file is saved."] = "로컬 시트 파일이 저장될 때마다 빌드합니다.",
            ["Check"] = "검사",
            ["Read and validate the sheets. Writes nothing."] = "시트를 읽고 검사만 합니다. 파일은 쓰지 않습니다.",
            ["Build"] = "빌드",
            ["Write generated code and data (Ctrl+Shift+Alt+B)."] = "코드와 데이터를 생성해 씁니다 (Ctrl+Shift+Alt+B).",
            ["Running"] = "실행 중",
            ["Failed"] = "실패",
            ["Checked"] = "검사 완료",
            ["Built"] = "빌드 완료",
            ["Up to date"] = "최신 상태",
            ["{0} is not in this project. The generated code will not compile until it is added."] = "이 프로젝트에 {0} 패키지가 없습니다. 추가하기 전에는 생성된 코드가 컴파일되지 않습니다.",

            // No config
            ["This project has no ugs.json yet. It holds the sources and output folders; the ugs command line reads the same file."] = "이 프로젝트에는 아직 ugs.json이 없습니다. 소스와 출력 폴더를 담는 파일이며, ugs 명령줄 도구도 같은 파일을 읽습니다.",
            ["Create ugs.json"] = "ugs.json 만들기",
            ["ugs.json could not be read: "] = "ugs.json을 읽을 수 없습니다: ",
            ["Open"] = "열기",
            ["Reload"] = "다시 읽기",

            // Problems
            ["Reading the sheets…"] = "시트를 읽는 중…",
            ["Nothing has run yet."] = "아직 실행한 적이 없습니다.",
            ["No problems."] = "문제가 없습니다.",
            ["Copy all"] = "모두 복사",
            ["Open file"] = "파일 열기",
            ["Copy"] = "복사",
            ["Copy message"] = "메시지 복사",
            ["Copy location"] = "위치 복사",
            ["Copy fix"] = "해결 방법 복사",
            ["Double-click a problem to open its file. Right-click to copy it."] = "문제를 더블클릭하면 그 파일이 열리고, 우클릭하면 복사할 수 있습니다.",
            ["[UGS] The file of '{0}' was not found among the sources."] = "[UGS] 소스에서 '{0}' 파일을 찾지 못했습니다.",
            ["Click to show or hide the problems of the last run."] = "클릭하면 마지막 실행의 문제 목록을 열거나 닫습니다.",

            // Tree and selection
            ["Other"] = "기타",
            ["Run Check to see the tables as the build reads them."] = "검사를 실행하면 빌드가 읽은 그대로 테이블을 볼 수 있습니다.",
            ["Read only the selected sheets again and validate. Writes nothing."] = "선택한 시트만 다시 읽어 검사합니다. 파일은 쓰지 않습니다.",
            ["Read only the selected sheets again and write the result."] = "선택한 시트만 다시 읽어 결과를 씁니다.",
            ["Build {0}"] = "{0} 빌드",
            ["Build ({0})"] = "빌드({0})",
            ["Tables built again: {0}"] = "다시 빌드되는 테이블: {0}",
            ["Enums read again: {0}"] = "다시 읽는 enum: {0}",
            ["Check {0}"] = "{0} 검사",
            ["Config table: one row, fields written down the sheet.\n"] = "설정 테이블: 한 행짜리이며 필드를 시트 아래로 적습니다.\n",
            ["Reads these again; every other sheet comes from the last build on this machine.\n"] = "아래 항목만 다시 읽고, 나머지 시트는 이 PC의 마지막 빌드에서 가져옵니다.\n",
            [" (all sheets)"] = " (모든 시트)",
            ["Adds columns to {0}, matched by key."] = "{0}에 열을 추가합니다 (키로 연결).",
            ["Defines {0}."] = "{0}을(를) 정의합니다.",
            ["Adds rows to {0}."] = "{0}에 행을 추가합니다.",

            // Table preview and find
            ["{0}  ·  part of {1}"] = "{0}  ·  {1}의 일부",
            ["⌘F to find"] = "⌘F로 찾기",
            ["Ctrl+F to find"] = "Ctrl+F로 찾기",
            ["Key"] = "키",
            ["Search {0}"] = "{0}에서 찾기",
            ["This table has no key"] = "이 테이블에는 키가 없습니다",
            ["All"] = "전체",
            ["Search every column"] = "모든 열에서 찾기",
            ["Rows whose cell contains this text. Esc closes."] = "이 글자가 들어 있는 행만 보여 줍니다. Esc로 닫습니다.",
            ["Row"] = "행",
            ["{0} row {1}"] = "{0} {1}행",
            ["No rows match."] = "맞는 행이 없습니다.",
            ["Find a table, sheet, enum or workbook by name. Enter selects the next match, Shift+Enter the one before."] = "테이블, 시트, enum, 워크북을 이름으로 찾습니다. Enter는 다음 항목, Shift+Enter는 이전 항목을 선택합니다.",
            ["No match"] = "없음",
            ["View"] = "보기",
            ["How the table looks. Kept for you only, in UserSettings."] = "표를 어떻게 보여줄지. UserSettings에 나만의 설정으로 저장됩니다.",
            ["Grid lines"] = "격자선",
            ["None"] = "없음",
            ["Between rows"] = "가로선",
            ["Between columns"] = "세로선",
            ["Both"] = "모두",
            ["Text alignment"] = "텍스트 정렬",
            ["Auto (numbers right)"] = "자동 (숫자는 오른쪽)",
            ["Left"] = "왼쪽",
            ["Center"] = "가운데",
            ["Right"] = "오른쪽",
            ["Striped rows"] = "줄무늬 배경",
            ["Enum"] = "Enum",
            ["Member"] = "멤버",
            ["Value"] = "값",
            ["Comment"] = "설명",
            ["{0} of {1}"] = "{1}개 중 {0}개",

            // Settings: sources
            ["Sources"] = "소스",
            ["Add source"] = "소스 추가",
            ["Excel workbook"] = "엑셀 워크북",
            ["CSV folder"] = "CSV 폴더",
            ["Google Drive folder"] = "구글 드라이브 폴더",
            ["Remove this source"] = "이 소스 제거",
            ["Folder"] = "폴더",
            ["File or folder"] = "파일 또는 폴더",
            ["A folder of .csv files. Each file is a sheet."] = ".csv 파일이 들어 있는 폴더. 파일 하나가 시트 하나입니다.",
            ["An .xlsx file, or a folder whose .xlsx files are all read."] = ".xlsx 파일, 또는 안의 .xlsx를 모두 읽을 폴더.",
            ["Not found: "] = "찾을 수 없음: ",
            ["The id from drive.google.com/drive/folders/<id>, or paste the whole URL. Every Google spreadsheet and .xlsx file directly in the folder is read; names starting with # are skipped."] = "drive.google.com/drive/folders/<id>의 id, 또는 URL 전체를 붙여넣으세요. 폴더 바로 아래의 구글 스프레드시트와 .xlsx를 모두 읽고, #으로 시작하는 이름은 건너뜁니다.",
            ["Spreadsheet"] = "스프레드시트",
            ["The id from the URL, or paste the whole URL."] = "URL의 id, 또는 URL 전체를 붙여넣으세요.",
            ["Access"] = "접근 방식",
            ["Public link and API key read spreadsheets shared as 'anyone with the link'. A service account reads private ones shared with its e-mail."] = "공개 링크와 API 키는 '링크가 있는 모든 사용자'로 공유된 시트를 읽습니다. 서비스 계정은 그 이메일에 공유된 비공개 시트를 읽습니다.",
            ["Public link"] = "공개 링크",
            ["API key"] = "API 키",
            ["Service account"] = "서비스 계정",
            ["Access token"] = "액세스 토큰",
            ["Custom provider"] = "커스텀 프로바이더",
            ["Command"] = "명령 실행",
            ["Runs in the project folder; what it prints is used: an access token, JSON with access_token, or a service account's JSON key. Works in the CLI and CI too. Use full paths: the editor may not see your terminal's PATH."] = "프로젝트 폴더에서 실행하고, 출력한 내용을 씁니다: 액세스 토큰, access_token이 든 JSON, 또는 서비스 계정 JSON 키. CLI와 CI에서도 동작합니다. 에디터는 터미널의 PATH를 모를 수 있으니 전체 경로를 쓰세요.",
            ["No credential provider was found. Add a class that implements UGS.Core.Sources.IGoogleCredentialProvider to an editor assembly; it is found by itself and listed here by its class name, or by the name in [UgsCredentialProvider(\"name\")]."] = "자격 증명 프로바이더가 없습니다. UGS.Core.Sources.IGoogleCredentialProvider를 구현한 클래스를 에디터 어셈블리에 추가하세요. 따로 등록하지 않아도 찾아서, 클래스 이름 또는 [UgsCredentialProvider(\"name\")]의 이름으로 여기에 보여 줍니다.",
            ["Provider"] = "프로바이더",
            ["The class that supplies the credentials. Only the editor runs it: the CLI and CI need auth type \"command\", or an offline build."] = "자격 증명을 주는 클래스. 에디터에서만 실행됩니다. CLI와 CI에서는 \"command\" 방식이나 오프라인 빌드를 쓰세요.",
            ["No credential provider named '{0}' is in the project."] = "프로젝트에 '{0}' 이름의 자격 증명 프로바이더가 없습니다.",
            ["Key variable"] = "키 환경 변수",
            ["Environment variable that holds the API key."] = "API 키가 들어 있는 환경 변수.",
            ["Or the key itself. Saved to ugs.json as plain text."] = "또는 키 자체. ugs.json에 평문으로 저장됩니다.",
            ["Key file"] = "키 파일",
            ["The service account's JSON key. Keep it out of version control."] = "서비스 계정의 JSON 키. 버전 관리에 올리지 마세요.",
            ["Token variable"] = "토큰 환경 변수",
            ["Environment variable that holds an OAuth access token."] = "OAuth 액세스 토큰이 들어 있는 환경 변수.",
            ["Google lists a folder only through the Drive API: choose an API key (for a link-shared folder), a service account or an access token."] = "구글은 폴더 목록을 Drive API로만 알려 줍니다. API 키(링크 공유 폴더), 서비스 계정, 액세스 토큰 중 하나를 고르세요.",
            ["Snapshot folder"] = "스냅샷 폴더",
            ["Optional. Each workbook is saved here as a folder of CSV, for diffs and offline builds."] = "선택 사항. 워크북마다 CSV 폴더로 저장해 변경 비교와 오프라인 빌드에 씁니다.",
            ["Tabs"] = "탭",
            ["Optional. Name:gid pairs, e.g. Item:0, Monster:123456. Empty reads every tab."] = "선택 사항. 이름:gid 쌍 (예: Item:0, Monster:123456). 비우면 모든 탭을 읽습니다.",
            ["Optional. The fetched CSV is saved here, for diffs and offline builds."] = "선택 사항. 받아 온 CSV를 여기에 저장해 변경 비교와 오프라인 빌드에 씁니다.",
            ["Browse"] = "찾아보기",
            ["File…"] = "파일…",
            ["Folder…"] = "폴더…",

            // Settings: output and options
            ["Output"] = "출력",
            ["Folders"] = "폴더",
            ["Code"] = "코드",
            ["Generated .g.cs files. Keep it under Assets so Unity compiles them."] = "생성되는 .g.cs 파일. Unity가 컴파일하도록 Assets 아래에 두세요.",
            ["Data"] = "데이터",
            [".bytes files and the manifest. Under StreamingAssets they stay plain files that can be watched and patched."] = ".bytes 파일과 매니페스트. StreamingAssets 아래에 두면 감시와 패치가 가능한 일반 파일로 남습니다.",
            ["Transformed CSV"] = "변환된 CSV",
            ["Optional. Each table as CSV after the sheet rules, for review."] = "선택 사항. 시트 규칙을 적용한 뒤의 테이블을 검토용 CSV로 씁니다.",
            ["Write schema file"] = "스키마 파일 쓰기",
            ["tables.schema.json next to the data, for tools without generated code."] = "생성 코드가 없는 도구를 위해 데이터 옆에 tables.schema.json을 씁니다.",
            ["Generated code"] = "생성 코드",
            ["Namespace"] = "네임스페이스",
            ["Tables class"] = "테이블 클래스",
            ["The class that holds every table: Load, LoadAsync, Watch."] = "모든 테이블을 담는 클래스: Load, LoadAsync, Watch.",
            ["Serializer"] = "직렬화",
            ["memorypack: fastest to load, C# only.\nmessagepack: readable from other languages."] = "memorypack: 로딩이 가장 빠르고 C# 전용.\nmessagepack: 다른 언어에서도 읽을 수 있음.",
            ["Sheet and build options"] = "시트와 빌드 옵션",
            ["Header rows"] = "헤더 행",
            ["What the header rows of a table sheet hold, top to bottom. Data starts on the next row."] = "테이블 시트 헤더 행의 내용(위에서 아래 순). 데이터는 그다음 행부터 시작합니다.",
            ["Array separator"] = "배열 구분자",
            ["Separates array elements in one cell. [a, b] is accepted too."] = "한 셀 안의 배열 원소를 나눕니다. [a, b] 형식도 됩니다.",
            ["CSV reader"] = "CSV 리더",
            ["auto and builtin use the built-in reader; sep uses Sep. Same results."] = "auto와 builtin은 내장 리더, sep은 Sep을 씁니다. 결과는 같습니다.",
            ["auto and builtin use the built-in reader. sep needs Unity 7 (.NET 8+)."] = "auto와 builtin은 내장 리더를 씁니다. sep은 Unity 7(.NET 8+)이 필요합니다.",
            ["Parallel"] = "병렬 처리",
            ["Convert and encode tables on several threads. The output is the same either way."] = "여러 스레드로 테이블을 변환하고 인코딩합니다. 결과는 같습니다.",
            ["Build label"] = "빌드 라벨",
            ["Optional text written to the manifest, such as a version."] = "선택 사항. 버전처럼 매니페스트에 적을 글자.",
            ["Open ugs.json"] = "ugs.json 열기",
            ["Reveal"] = "파일 위치 열기",

            // Settings: signing
            ["Signing (signed builds)"] = "서명 (서명된 빌드)",
            ["Signing (unsigned builds)"] = "서명 (서명 없는 빌드)",
            ["Public key"] = "공개 키",
            ["Baked into the generated code. Safe to ship."] = "생성 코드에 들어갑니다. 배포해도 안전합니다.",
            ["Private key file"] = "개인 키 파일",
            ["Signs the manifest. Keep it out of version control."] = "매니페스트에 서명합니다. 버전 관리에 올리지 마세요.",
            ["Private key variable"] = "개인 키 환경 변수",
            ["Environment variable with the private key text, for CI. Wins over the file."] = "CI용으로 개인 키 내용을 담은 환경 변수. 파일보다 우선합니다.",
            ["Stop signing"] = "서명 중단",
            ["Create key pair"] = "키 쌍 만들기",
            ["Verify data folder"] = "데이터 폴더 검증",
            ["Check every data file against the manifest, and the signature if there is one."] = "모든 데이터 파일을 매니페스트와 대조하고, 서명이 있으면 서명도 확인합니다.",
            ["Add"] = "추가",
            ["Not now"] = "나중에",
            ["Does not match the manifest: "] = "매니페스트와 다름: ",
            ["The signature does not match the public key."] = "서명이 공개 키와 맞지 않습니다.",
            ["The manifest is not signed."] = "매니페스트에 서명이 없습니다.",
            ["{0} files match the manifest; the signature is valid."] = "파일 {0}개가 매니페스트와 일치하고, 서명도 유효합니다.",
            ["{0} files match the manifest."] = "파일 {0}개가 매니페스트와 일치합니다.",

            // Settings: this window
            ["Editor"] = "에디터",
            ["Language"] = "언어",
            ["The language of this window. Kept for you only, in UserSettings."] = "이 창의 언어. UserSettings에 저장되며 나에게만 적용됩니다.",
            ["System ({0})"] = "시스템 설정 ({0})",
        };
    }
}
