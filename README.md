# XCENA Terminal

WinUI 3 기반 SSH 터미널 클라이언트. VT100/ANSI 에뮬레이션은 WebView2에 올린 xterm.js가,
SSH 전송은 SSH.NET이 담당한다.

## 구조

```
MainWindow                        사이드바 + 제목 줄 (전역 탭 스트립 없음)
 └─ Controls/SessionSurface       패널 중첩 트리를 소유
     ├─ Controls/PaneNode         트리 노드 (leaf = 패널, split = n-ary 분할)
     ├─ Controls/PaneSplitter     드래그로 크기 조절하는 분할선
     └─ Controls/PaneGroup        패널 1개 = TabView 1개 (탭 여러 개 보유)
         └─ Controls/TerminalView WebView2 ↔ xterm.js 브리지 (탭 1개 = 세션 1개)
             ├─ Assets/xterm/terminal.html   xterm 초기화, 키/마우스 처리
             └─ Services/SshSession          SSH.NET ShellStream 입출력

Services/ProfileStore      %APPDATA%\XCENA Terminal\profiles.json
Services/SecretProtector   DPAPI(CurrentUser)로 비밀번호·passphrase 암호화
Services/KnownHostsStore   %APPDATA%\XCENA Terminal\known_hosts.json
Services/RecentStore       %APPDATA%\XCENA Terminal\recent.json (비밀 없음)
Services/AppearanceStore   %APPDATA%\XCENA Terminal\appearance.json
Services/LayoutStore       %APPDATA%\XCENA Terminal\layout.json (사이드바 폭/위치)
Services/SshTeardown       SSH 연결 해제를 UI 스레드 밖에서 처리
```

### 화면 배치

VS Code의 에디터 그룹과 같은 모델이다. 화면은 **패널** 여러 개로 나뉘고, 패널마다 자기
탭 스트립이 있어 **한 패널이 여러 세션을 가질 수 있다**. 전역 탭 바는 없다.

패널은 **중첩 트리**(`PaneNode` / `PaneLeafNode` / `PaneSplitNode`)로 관리한다. 어떤 패널을
분할하면 **그 패널의 영역만** 나뉘고 나머지 패널의 크기·방향은 그대로다. 예를 들어 좌우로
나눈 뒤 왼쪽 패널을 위아래로 다시 나누면, 오른쪽 패널은 전체 높이를 유지한다.

분할 노드는 이진이 아니라 **n-ary**다. 같은 방향으로 또 나누면 그 노드에 형제가 하나 더
들어가므로 패널들이 균등하게 유지된다(한쪽을 계속 반씩 쪼개지 않는다). 방향이 바뀔 때만
새 중첩 노드가 생긴다.

| 조작 | 결과 |
|---|---|
| `Alt+Shift++` | 현재 탭을 떼어 **오른쪽**에 새 패널로 (패널에 탭이 2개 이상일 때) |
| `Alt+Shift+-` | 현재 탭을 떼어 **아래쪽**에 새 패널로 |
| 제목 줄 → 모든 세션 좌우/위아래로 펼치기 | 세션마다 패널 하나씩 |
| 제목 줄 → 단일 패널 (모두 합치기) | 모든 탭을 한 패널로 되돌림 |

**탭을 끌어다 놓는 위치가 동작을 정한다.**

| 떨어뜨린 곳 | 결과 |
|---|---|
| 대상 패널의 탭 스트립 또는 가운데 | 그 패널로 **합쳐짐** (비워진 패널은 자동으로 사라짐) |
| 패널의 왼쪽/오른쪽/위/아래 **가장자리** | 그 방향에 **새 패널** 생성 |

가장자리 판정 폭은 패널 크기의 25%(40~160px)다. 드래그하는 동안 들어갈 영역이 반투명
사각형으로 미리 표시된다.

주의: WinUI `TabView`는 자기 안에서의 순서 변경만 처리하고 **인스턴스 사이 이동은 해주지
않는다.** `AllowDrop`을 켜도 `TabStripDragOver`/`TabStripDrop`이 오지 않았기 때문에,
`TabDragCompleted`에서 커서 위치(`GetCursorPos` → 클라이언트 좌표 → `RasterizationScale`로
XamlRoot 좌표)를 읽어 그 아래 패널과 가장자리 여부를 직접 판정한다. 드래그 중 미리보기도
같은 이유로 이벤트가 아니라 40ms 타이머로 커서를 폴링해서 그린다.

### 세션 관리 패널

- 패널과 터미널 영역 사이 분할선을 **마우스로 끌어 폭 조절**(160~620px)
- 패널 헤더를 **끌어서 좌/우로 도킹** (드래그 중 반쪽 미리보기 표시), 헤더 우클릭 메뉴에도
  `Dock left` / `Dock right` / `Hide panel`
- 폭과 위치는 `layout.json`에 저장된다

패널 사이 분할선(`PaneSplitter`)은 항상 색이 칠해져 있고 마우스로 끌어 크기를 조절한다.
드래그 좌표는 **창 기준**(`GetCurrentPoint(null)`)으로 읽는다 — 포인터가 캡처된 동안에는
특정 요소 기준 변환이 갱신되지 않아 이동량이 0으로 고정되기 때문이다.

각 패널은 자기 크기에 맞춰 원격 PTY에 `window-change`를 따로 보낸다. 활성 패널 테두리는
두께를 항상 1px로 고정하고 색만 바꿔, 강조 토글이 레이아웃(=터미널 그리드 크기)을 흔들지
않게 했다.

### 색상

제목 줄의 색상 버튼에서 **배경색·글자색**을 지정한다. 하나의 `ColorPicker`가 라디오로 선택한
대상을 편집하고, 아래 미리보기에서 두 색의 가독성을 함께 확인한다. "기본값"은 닫지 않고
초기값으로 되돌려 미리 볼 수 있게 한다.

선택한 값은 `appearance.json`에 저장되고, 열려 있는 모든 터미널과 이후에 만들어지는 세션에
적용된다. 커서 색은 글자색을 따라가고 선택 영역은 두 색을 30%로 섞어 만든다 — 어떤 배경에서도
보이도록.

xterm에는 `h` 태그로 테마 패치(JSON)를 보낸다. 페이지가 아직 로딩 중이면 값을 기억해 두고
`ready` 이후에 다시 보낸다. 패치는 기본 팔레트(16색 등)를 유지한 채 병합된다.

### 자동 재접속

연결이 끊기면 **5초마다 자동으로 다시 접속**한다. 오버레이에 남은 시간과 시도 횟수가
표시되고, "자동 재접속 중지"로 멈출 수 있다. 재접속하지 않는 경우는 다음과 같다.

- 셸이 정상 종료된 경우(`exit`) — 사용자의 의도이므로 되살리지 않는다
- 인증 실패 — 잘못된 자격증명으로 5초마다 두드리면 계정 잠금이나 fail2ban을 유발한다
- 호스트 키 불일치, 키 파일 없음 — 사람이 확인해야 하는 문제다

### 브리지 프로토콜

WebView2 문자열 메시지의 첫 글자가 태그다.

| 방향 | 태그 | 내용 |
|---|---|---|
| C# → JS | `o` | 셸 출력 (base64 UTF-8 바이트) |
| C# → JS | `m` `p` `f` `z` `x` `s` | 알림 / 붙여넣기 / 포커스 / fit / clear / 폰트 크기 |
| JS → C# | `i` | 키 입력 (base64 UTF-8) |
| JS → C# | `r` | 그리드 크기 `cols,rows` → `ShellStream.ChangeWindowSize` |
| JS → C# | `t` `c` `v` `k` | 제목 / 복사 / 붙여넣기 요청 / 창 단위 명령 |

출력은 UI 틱마다 한 메시지로 합쳐서 보낸다(대량 출력 시 메시지 폭주 방지).

## 키

| 키 | 동작 |
|---|---|
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | 복사 / 붙여넣기 (우클릭도 동일: 선택 있으면 복사, 없으면 붙여넣기) |
| `Ctrl+Shift+T` | 새 접속 (현재 패널에 새 탭) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 현재 패널 안에서 다음/이전 탭 |
| `Alt+Shift++` | 현재 탭을 오른쪽 새 패널로 분할 |
| `Alt+Shift+-` | 현재 탭을 아래쪽 새 패널로 분할 |
| `Ctrl+Shift+W` | 현재 세션 닫기 |
| `Ctrl+Shift+B` | 프로필 사이드바 토글 |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | 글자 크기 확대 / 축소 / 기본값 |

배치는 제목 줄의 배치 버튼으로도 바꿀 수 있다.

### 세션 열기

탭 스트립의 `+`는 바로 대화상자를 띄우지 않고 메뉴를 보여준다.

```
최근 접속            ← recent.json, 최신 8개
  BMC2                      root@192.168.120.54
  BMC1                      root@192.168.120.91
  기록 지우기
────────────
저장된 접속          ← profiles.json
  192.168.120.157           bcqdev@192.168.120.157
  BMC1                      root@192.168.120.91
────────────
새 접속…             ← 전체 대화상자
```

최근 항목으로 접속하면 같은 엔드포인트의 저장된 프로필을 먼저 찾아 쓴다 — 그래야 DPAPI로
보관된 비밀번호가 그대로 적용된다. 없으면 임시 프로필로 접속하며 비밀번호를 묻는다.
히스토리에는 **성공한 접속만** 기록하고 비밀은 담지 않는다.

탭 우클릭 메뉴: `복제`(같은 서버로 세션 추가, 메모리의 자격증명 재사용) / 오른쪽·아래 패널로
분할 / 닫기.

WebView2가 키보드 포커스를 가지므로 XAML 액셀러레이터가 보이지 않는다. 위 창 단위 조합은
`terminal.html`이 가로채 `k` 태그로 호스트에 넘긴다.

## 색상 테마

강조색은 버건디(`#8C2332`)다. `App.xaml`에서 `AccentFillColor*`와 **`AccentButton*` 별칭
키**를 함께 덮는다 — WinUI 3의 accent 버튼 스타일이 참조하는 쪽은 후자라서 앞의 것만 바꾸면
버튼은 파란색으로 남는다. 이 오버라이드 사전은 반드시 `XamlControlsResources` **뒤에** 병합해야
한다(병합 사전은 나중 것이 이긴다). 코드에서 칠하는 강조면(활성 패널 테두리, 분할선 hover,
드롭 미리보기)은 `Controls/AppAccent.cs`가 같은 값을 들고 있다.

## 종료 성능

`SshClient.Dispose()`는 disconnect 메시지를 보내고 메시지 루프 스레드를 join할 때까지
블로킹한다. 죽은 링크에서는 타임아웃까지 기다리며, 이걸 UI 스레드에서 세션 수만큼 반복하면
창이 닫히지 않는 것처럼 보인다. `Services/SshTeardown`이 해제를 백그라운드로 넘기고, 창을
닫을 때만 250ms 동안 기다려 준다(정상 링크는 그 안에 인사를 마치고, 죽은 링크는 붙잡지 않는다).

측정값 — 세션 4개, disconnect 1건당 2초로 가정:

| | UI 스레드 블록 | 프로세스 종료 |
|---|---|---|
| 동기 해제 | 8036 ms | 8155 ms |
| 백그라운드 해제 | 275 ms | 485 ms |

## 보안

- 비밀번호·키 passphrase는 저장을 선택했을 때만 DPAPI(CurrentUser)로 암호화해 보관한다.
  다른 Windows 사용자나 다른 PC에서는 복호화되지 않는다.
- 호스트 키는 TOFU 방식으로 첫 접속 시 고정(pin)한다. 이후 키가 바뀌면 접속을 **거부**하고
  경고한다. 서버를 재설치한 경우 프로필 우클릭 → "호스트 키 신뢰 해제" 후 재접속한다.

## 빌드 / 실행

패키지(MSIX) 앱으로 구성돼 있다. Visual Studio에서 `XCENA_Terminal_Dev (Package)`를
시작 프로젝트로 두고 F5로 실행한다.

`bin\x64\Debug\net8.0-...\XCENA_Terminal_Dev.exe`는 **더블클릭으로 실행되지 않는다.**
패키지 컨텍스트가 없으면 WinUI 활성화가 `REGDB_E_CLASSNOTREG`로 실패한다. 탐색기에서
바로 실행할 exe가 필요하면 언패키지로 빌드한다(Windows App SDK 2.3 런타임 필요 — 이미
설치돼 있음):

```powershell
dotnet build XCENA_Terminal_Dev\XCENA_Terminal_Dev\XCENA_Terminal_Dev.csproj `
  -c Debug -p:Platform=x64 -p:WindowsPackageType=None `
  -p:OutputPath=XCENA_Terminal_Dev\XCENA_Terminal_Dev\bin\Run-x64\
```

산출물: `XCENA_Terminal_Dev\XCENA_Terminal_Dev\bin\Run-x64\XCENA_Terminal_Dev.exe`

`OutputPath`만 바꾸고 `BaseIntermediateOutputPath`는 건드리지 말 것 — `obj` 위치를 옮기면
기존 `obj\x64`의 생성 코드가 중복 컴파일되어 CS0579로 깨진다. 언패키지 빌드는 `obj`를
공유하므로, 이후 VS에서 패키지 빌드를 할 때 한 번 다시 빌드하면 정상 상태로 돌아온다.

MSIX로 배포/등록할 때는 Windows **개발자 모드**가 켜져 있어야 하고, Debug 구성은
`Microsoft.VCLibs.140.00.Debug.UWPDesktop` 프레임워크 패키지가 필요하다
(`Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs.Desktop\14.0\Appx\Debug\x64\`).

`PublishTrimmed`는 끈 상태다 — SSH.NET이 암호 알고리즘을 리플렉션으로 찾기 때문에
트리밍하면 런타임에 깨진다.

## 렌더링 참고

- WebGL 렌더러 애드온을 사용한다. 박스 드로잉 문자를 폰트가 아니라 직접 그리므로 TUI 프레임에
  이음선이 생기지 않고, 글리프를 셀에 맞춰 클리핑하므로 폴백 한글 폰트가 열 정렬을 깨뜨리지 않는다.
  WebGL을 못 쓰면 자동으로 DOM 렌더러로 내려간다.
- 폰트 순서는 `D2Coding → Cascadia Mono → Consolas → GulimChe → Malgun Gothic`. D2Coding을
  설치하면 한글 폭이 영문의 정확히 2배가 되어 가장 깔끔하다.
- `Assets/xterm/*.js`는 npm 배포본을 **바이트 단위로** 복사한 것이다. 텍스트로 재인코딩하면
  비ASCII 바이트가 깨져 `SyntaxError`가 난다. 교체할 때 주의.

## 아직 없는 것

- SFTP 파일 전송
- 세션 로그 저장, 스크롤백 검색
- 점프 호스트 / 포트 포워딩
- SSH 에이전트(Pageant, OpenSSH agent) 연동 — 현재는 키 파일 직접 지정만 지원
