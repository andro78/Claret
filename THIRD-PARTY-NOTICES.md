# 제3자 고지

Claret이 배포본에 함께 담아 나가는 구성 요소들이다. MIT 라이선스는 저작권 고지를
배포물에 포함할 것을 요구하므로, 그 전문을 여기 그대로 옮겨 둔다.

빌드 도구(`Microsoft.Windows.SDK.BuildTools`)는 빌드에만 쓰이고 패키지에 들어가지 않으므로
목록에 없다.

---

## xterm.js

터미널 에뮬레이션. `Claret/Claret/Assets/xterm/`에 npm 배포본을 그대로 담아 나간다.
같은 라이선스 파일이 그 폴더에도 함께 배포된다(`LICENSE-xterm.txt`,
`LICENSE-xterm-addon-webgl.txt`).

- https://github.com/xtermjs/xterm.js — MIT
- `@xterm/addon-fit`, `@xterm/addon-webgl` 애드온 포함

```
Copyright (c) 2017-2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)
Copyright (c) 2014-2016, SourceLair Private Company (https://www.sourcelair.com)
Copyright (c) 2012-2013, Christopher Jeffrey (https://github.com/chjj/)
Copyright (c) 2018, The xterm.js authors (https://github.com/xtermjs/xterm.js)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

---

## SSH.NET

SSH 전송과 SFTP.

- https://github.com/sshnet/SSH.NET — MIT
- Copyright © Renci 2010-2026

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

---

## .NET 런타임 라이브러리

`System.IO.Ports`(시리얼 포트), `System.Management`(포트 이름 조회),
`System.Security.Cryptography.ProtectedData`(DPAPI).

- https://github.com/dotnet/runtime — MIT
- © Microsoft Corporation. All rights reserved.

MIT 전문은 위 xterm.js 항목과 동일하다.

---

## Windows App SDK / WebView2

WinUI 3 프레임워크와 WebView2 SDK. Microsoft 소프트웨어 라이선스 조건에 따라 재배포된다.

- Windows App SDK — https://github.com/microsoft/WindowsAppSDK, © Microsoft Corporation
- WebView2 SDK — https://aka.ms/webview2, © Microsoft Corporation

라이선스 전문은 각 NuGet 패키지의 `license.txt` / `LICENSE.txt`에 들어 있다.
