# eslee MeshDrive

같은 공유기에 연결된 Windows PC와 Android 파일을 원본 그대로 탐색·재생·복사하는 내부망 앱입니다. Wi-Fi↔유선 LAN을 지원하며 별도 서버/계정/Relay가 필요하지 않습니다.

현재 **0.2.0 실기기 검증 후보**입니다. 자동 테스트 통과는 실기기 검증 완료를 의미하지 않습니다. [최종 검증 목록](docs/MANUAL-VERIFICATION.md)을 통과한 뒤 중요한 데이터에 적용하세요.

## 설치

- Windows 10 2004 이상/Windows 11 x64: `MeshDriveSetup.exe`. .NET 런타임 포함, 설치 관리자 권한 필요.
- Android 8.0 이상: `MeshDrive-release.apk`. 직접 설치용 서명 APK이며 스토어 배포판이 아닙니다.
- 같은 공유기의 일반 LAN을 사용합니다. 게스트 Wi-Fi/AP 격리/분리 VLAN에서는 발견이 막힐 수 있습니다.
- 발견된 기기에 연결 요청 후 **양쪽 6자리 SAS가 일치할 때만 양쪽 승인**합니다. 수신 만료는 상대 시각과 관계없이 로컬 기준 최대 2분입니다.
- 공유할 폴더만 직접 등록합니다. Android는 시스템 폴더 선택기에서 접근을 허용합니다.

Windows EXE는 Authenticode 서명하지 않아 SmartScreen 경고가 나타날 수 있습니다. 출처와 배포 SHA-256을 확인하세요.

## 기능

- 기기/폴더 탐색, 사진 썸네일과 원본 사진 보기, 원본 HTTPS Range 스트리밍
- Windows 음악/영상 플레이어 선택, Android 연결 프로그램 선택
- 받기/보내기 및 제3 기기 직접 복사 지시
- QuickSend 청크 SHA-256/Merkle 검증, 동일 작업 재시도 시 이어받기, 기존 이름 보존
- 공유 중지, 기기별 권한, 페어링 해제, Windows 자동 시작 및 선택적 TrayFolder 연동
- 별도 등록한 폴더의 단방향/양방향 동기화, 충돌 사본, 이전 버전 보관/복원

음악/영상은 변환하지 않습니다. 플레이어에 임시 `127.0.0.1` 주소를 전달하므로 실제 플레이어의 형식/Range 지원이 필요합니다. 사진은 파일당 256 MiB, 원본 캐시 1 GiB, 썸네일 캐시 256 MiB 제한입니다.

제3 기기 복사는 송신·수신·지시 기기의 페어링과 원본 Download/대상 Upload 권한이 필요합니다. 파일은 송신→수신으로 이동하며 지시 기기는 제어만 합니다. 일반 공유에는 원격 삭제/이름 변경을 제공하지 않습니다.

## 동기화

**중요하지 않은 테스트 폴더로 먼저 검증하세요.** 동기화는 사용자 승인 아래 수정·삭제를 전파하며 일반 공유와 별개입니다.

1. 양쪽에서 동기화 폴더와 허용 기기를 등록합니다.
2. Windows의 선택 폴더 동기화 화면에서 양쪽 폴더·방향을 선택하고 규칙을 활성화합니다.
3. Windows Agent가 30초마다 검사하며 오프라인에서는 다음 주기에 재시도합니다. Android는 선택 폴더 API를 제공하고 자동 규칙은 Windows에서 관리합니다.
4. 동시 변경은 충돌 사본으로 보존합니다. 교체/삭제 전 이전 바이트를 앱 데이터에 보관하며 이전 버전 화면에서 복원합니다.

기본 보관 정책은 파일당 20개/30일입니다. 같은 기기의 이전 버전은 별도 디스크 백업을 대신하지 않습니다. Android 저장소 공급자의 기능/권한에 따라 교체·이름 변경이 실패할 수 있습니다. Android끼리 독립 예약 동기화는 이번 범위가 아닙니다.

## 종료·업데이트·데이터

Windows 창의 X는 Agent를 유지하며 전체 종료 메뉴에서 Agent도 종료합니다. TrayFolder 없이도 핵심 기능이 동작합니다. Android는 foreground service로 동작하지만 제조사 절전 정책에 영향을 받습니다. Android 재부팅 후 앱을 다시 실행하세요.

Windows 데이터: `%LOCALAPPDATA%\eslee\MeshDrive`. 같은 설치 프로그램으로 업데이트하면 설정·페어링·동기화 기준/이전 버전을 보존하도록 구성했습니다. 제거 시 데이터 삭제 질문의 기본은 '아니요'입니다. **'예'는 이전 버전 백업도 영구 삭제**합니다. 원본 공유 폴더는 제거 대상이 아닙니다.

Android 앱 삭제/데이터 지우기는 기기 키와 내부 이전 버전을 지웁니다. release 업데이트는 같은 서명 키가 필요합니다. debug APK와는 서명이 달라 바로 덮어 설치할 수 없으므로 필요한 데이터를 먼저 백업/복원해야 합니다.

외부망/Relay, iOS/macOS/Linux GUI, 가상 드라이브, 공개 링크는 이번 범위가 아닙니다.

## 개발 빌드

.NET SDK 10.0.302 이상, 설치 패키지는 Inno Setup 6가 필요합니다.

```powershell
dotnet build MeshDrive.slnx -c Release
dotnet test MeshDrive.slnx -c Release --no-build
./scripts/Build-Windows.ps1
```

산출물: `artifacts/installer/MeshDriveSetup.exe`.

Android는 JDK 21/SDK 36을 사용하며 한글 경로의 테스트 호환성을 위해 ASCII 임시 폴더에서 빌드합니다.

```powershell
./scripts/Build-Android.ps1 -JavaHome '<JDK21>' -AndroidSdk '<Android SDK>'
./scripts/Initialize-AndroidSigning.ps1 -JavaHome '<JDK21>'
./scripts/Build-Android.ps1 -Release -JavaHome '<JDK21>' -AndroidSdk '<Android SDK>'
```

release 빌드도 공통 코드의 debug 단위 테스트와 debug/release Lint를 실행합니다. 산출물: `artifacts/android/MeshDrive-release.apk`. 서명 키는 `%LOCALAPPDATA%\eslee\MeshDriveBuild\signing`에 유지되며 비밀번호는 현재 Windows 사용자 DPAPI로 보호합니다. 키/암호 자료는 Git에 넣지 않습니다. 개발 PC 교체 전 안전한 키 백업이 필요하며 DPAPI 파일만 다른 계정으로 옮겨서는 복호화할 수 없습니다.

자동 검증은 로컬 HTTPS 서버 간 SAS/mTLS/Range/전송/동기화, 디스크 손상·재개·충돌·복원, WPF 비표시 레이아웃, Android 단위/Robolectric 파일 어댑터 테스트입니다. 실제 Android SAF 공급자나 LAN 라우터를 대신하지 않습니다.

## 라이선스

MIT. [LICENSE](LICENSE). 고정 재사용한 QuickSend 코드는 `third_party`의 출처 기록을 참고하세요.
