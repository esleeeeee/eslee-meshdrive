# eslee MeshDrive

eslee MeshDrive는 같은 네트워크의 PC끼리 파일을 직접 다루는 Windows 앱입니다.

지금 저장소는 **Phase 0**입니다. GUI와 백그라운드 Agent를 따로 띄우고, Named Pipe로 Agent 상태를 확인하고 종료하는 실행 골격만 포함합니다.

문서 언어: **한국어**

## 현재 범위

포함:

- `MeshDrive.Core`, `MeshDrive.Protocol`, `MeshDrive.Agent`, `MeshDrive.Windows`
- GUI와 Agent의 별도 프로세스
- Named Pipe IPC (`get-status`, `shutdown`)
- GUI에서 Agent 상태 표시
- 창 닫기 시 Agent 유지, 재실행 시 재연결
- 전체 종료 명령에서만 Agent 종료
- 기본 빌드와 핵심 IPC 테스트

아직 없음:

- mDNS, LAN 통신, HTTPS, 페어링
- 파일 탐색, 스트리밍
- QuickSend, TrayFolder 연동
- 설치 프로그램

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

작업 관리자에서 `MeshDrive.Agent`와 `MeshDrive.Windows`를 구분해 확인할 수 있습니다.

## 프로젝트 구성

| 프로젝트 | 역할 |
|---|---|
| `MeshDrive.Core` | 제품 이름, 버전, 파이프/뮤텍스 이름 |
| `MeshDrive.Protocol` | Named Pipe JSON 프로토콜, 서버/클라이언트 |
| `MeshDrive.Agent` | 백그라운드 Agent 프로세스 |
| `MeshDrive.Windows` | WPF GUI |
| `MeshDrive.Tests` | 프로토콜, 재연결, 프로세스 수명주기 테스트 |

## 라이선스

MIT. [LICENSE](LICENSE)를 참고하세요.
