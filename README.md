# eslee MeshDrive

eslee MeshDrive는 같은 네트워크의 PC끼리 파일을 직접 다루는 Windows 앱입니다.

지금 저장소는 **Phase 1**입니다. GUI와 Agent는 따로 실행되고, Agent가 같은 공유기에서 MeshDrive 기기를 mDNS로 찾습니다.

문서 언어: **한국어**

## 현재 범위

포함:

- `MeshDrive.Core`, `MeshDrive.Protocol`, `MeshDrive.Agent`, `MeshDrive.Windows`
- GUI와 Agent의 별도 프로세스
- Named Pipe IPC (`get-status`, `get-peers`, `shutdown`)
- GUI에서 Agent 상태와 주변 MeshDrive 기기 표시
- `_meshdrive._tcp.local` mDNS 광고와 탐색
- Ethernet과 Wi-Fi를 함께 쓰는 LAN 발견
- 창 닫기 시 Agent 유지, 재실행 시 재연결
- 전체 종료 명령에서만 Agent 종료

아직 없음:

- 페어링, SAS, HTTPS 인증
- 파일 탐색, 스트리밍
- QuickSend, TrayFolder 연동
- 설치 프로그램
- 수동 IP 입력

## 요구 환경

- Windows 10 2004 이상 또는 Windows 11 x64
- .NET SDK 10.0.302 이상 (실행은 SDK를 설치한 개발 환경 기준)

## 빌드와 테스트

저장소 루트에서:

```powershell
dotnet build MeshDrive.slnx
dotnet test MeshDrive.slnx
```

## 실행

```powershell
dotnet run --project src/MeshDrive.Windows
```

GUI가 Agent를 찾고, 없으면 `MeshDrive.Agent.exe`를 같은 폴더에서 시작합니다.

- **창 닫기 (Agent 유지)** 또는 창의 X: GUI만 종료합니다. Agent는 남습니다.
- GUI를 다시 실행하면 기존 Agent에 재연결합니다. Agent 프로세스 ID와 시작 시각은 같고, 이 창 세션 값은 달라집니다.
- **MeshDrive 종료**: Agent에 종료 명령을 보낸 뒤 GUI도 닫습니다.
- **주변 MeshDrive**: 같은 공유기에서 발견된 다른 PC의 이름, 온라인 상태, IPv4 주소가 나타납니다. 이 PC 자신은 목록에 없습니다.

작업 관리자에서 `MeshDrive.Agent`와 `MeshDrive.Windows`를 구분해 확인할 수 있습니다.

같은 공유기에 Ethernet Desktop과 Wi-Fi Laptop을 두고 양쪽에서 MeshDrive를 실행하면 서로가 목록에 보여야 합니다. 처음 실행할 때 Windows 방화벽이 뜨면 개인 네트워크를 허용하세요.

## 프로젝트 구성

| 프로젝트 | 역할 |
|---|---|
| `MeshDrive.Core` | 제품 이름, Device ID, 발견 상태 |
| `MeshDrive.Protocol` | Named Pipe JSON 프로토콜, 서버/클라이언트 |
| `MeshDrive.Agent` | 백그라운드 Agent 프로세스 |
| `MeshDrive.Windows` | WPF GUI |
| `MeshDrive.Tests` | 프로토콜, 재연결, 프로세스 수명주기 테스트 |

## 라이선스

MIT. [LICENSE](LICENSE)를 참고하세요.
