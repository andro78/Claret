# 개인정보처리방침 / Privacy Policy

마지막 갱신: 2026-08-24

> 스토어 제출용으로는 이 파일을 공개 URL로 올려야 한다(GitHub Pages 또는 저장소 raw URL도
> 스토어가 받아 준다).

---

## 한국어

Claret은 로컬에서만 동작하는 터미널 클라이언트다. **개발자에게 어떤 데이터도 전송하지
않는다.** 원격 분석(telemetry), 사용 통계, 오류 보고, 광고, 계정 로그인이 모두 없다.

### 앱이 다루는 정보

| 항목 | 어디에 저장되는가 |
|---|---|
| 접속 정보 (호스트, 포트, 사용자 이름, 시리얼 포트 설정) | `%APPDATA%\Claret\profiles.json` |
| 비밀번호 · 키 passphrase | 같은 파일. Windows DPAPI로 **현재 사용자 계정에 묶어** 암호화 |
| 서버 호스트 키 지문 | `%APPDATA%\Claret\known_hosts.json` |
| 최근 접속 목록 | `%APPDATA%\Claret\recent.json` |
| 화면 설정 · 창 배치 · 다운로드 폴더 | `appearance.json`, `layout.json` |
| SFTP로 주고받은 파일 | 사용자가 지정한 폴더 |

모두 사용자 PC에만 있다. 개발자의 서버는 존재하지 않는다.

### 네트워크 사용

앱이 여는 연결은 **사용자가 직접 지정한 SSH 서버와 시리얼 포트뿐이다.** 그 서버로 보내는
자격 증명과 입력은 SSH 프로토콜로 암호화되어 해당 서버에만 전달된다. 그 서버가 데이터를
어떻게 취급하는지는 그 서버 운영자의 방침을 따른다.

터미널 화면은 WebView2에 올린 xterm.js가 그린다. 이 WebView2는 앱 폴더 안의 로컬 파일만
읽도록 가상 호스트 매핑이 걸려 있고, 인터넷에 접속하지 않는다.

### 수집하지 않는 것

위치, 연락처, 카메라·마이크, 브라우저 기록, 광고 식별자, 그 밖의 어떤 개인 식별 정보도
수집·전송·판매하지 않는다.

### 삭제 방법

`%APPDATA%\Claret` 폴더를 지우면 앱이 저장한 모든 정보가 없어진다. 앱을 제거해도 된다.
저장된 비밀번호는 Windows 사용자 계정에 묶여 있어 다른 계정이나 다른 PC로 옮기면
복호화되지 않는다.

### 연락처

- 버그·기능 문의: https://github.com/andro78/Claret/issues
- 개인정보 문의: andro78@msn.com

---

## English

Claret is a local-only terminal client. **No data is sent to the developer.** There is no
telemetry, no usage analytics, no crash reporting, no advertising, and no account sign-in.

### What the app stores

| Data | Where it is stored |
|---|---|
| Connection details (host, port, username, serial port settings) | `%APPDATA%\Claret\profiles.json` |
| Passwords and key passphrases | Same file, encrypted with Windows DPAPI, **bound to the current Windows user account** |
| Server host key fingerprints | `%APPDATA%\Claret\known_hosts.json` |
| Recent connections | `%APPDATA%\Claret\recent.json` |
| Appearance, window layout, download folder | `appearance.json`, `layout.json` |
| Files transferred over SFTP | Folders the user chooses |

All of it stays on the user's own machine. The developer operates no server.

### Network use

The only connections the app opens are to the SSH servers and serial ports **the user
specifies**. Credentials and keystrokes are encrypted by the SSH protocol and reach only that
server; how that server handles them is governed by its own operator's policy.

The terminal is rendered by xterm.js inside WebView2. That WebView2 is mapped to a local folder
inside the app package and does not reach the internet.

### What is not collected

No location, contacts, camera or microphone, browsing history, advertising identifiers, or any
other personal information is collected, transmitted, or sold.

### Deletion

Deleting `%APPDATA%\Claret` removes everything the app has stored. Saved passwords are tied
to the Windows user account and cannot be decrypted under another account or on another machine.

### Contact

- Bugs and feature requests: https://github.com/andro78/Claret/issues
- Privacy enquiries: andro78@msn.com
