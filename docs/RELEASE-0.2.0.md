# MeshDrive 0.2.0-rc.1 — LAN 실기기 검증 후보

Windows·Android용 내부망 제품 구현입니다. 서버나 Relay 없이 같은 공유기의 Wi-Fi/유선 LAN을 사용합니다. 패키지 내부 버전은 0.2.0이며 실기기 확인 전이므로 GitHub에서는 prerelease로 배포합니다.

## 추가한 기능

- 인증된 공유 탐색, 사진 썸네일/원본 캐시, 외부 플레이어 원본 Range 스트리밍
- QuickSend 호환 검증/재개 복사, 제3 기기가 지시하는 송신→수신 직접 복사
- 일반 공유와 분리된 선택 폴더 동기화, 충돌 사본, 이전 버전 보관/복원
- Android Client/Agent 및 SAF 선택 폴더, Windows 자동 시작/TrayFolder 보조 제어
- 다중 기기 권한, LAN 재발견 보강, Windows 탐색 트리/설정 UI

기존 Phase 0–2를 유지하며 수신 페어링 세션의 로컬 2분 상한 테스트도 유지합니다. 완료 기록만 신뢰하지 않고 현재 저장 파일을 재검증하며 사용자 수정본은 재복사 시 보존합니다.

## 자동 검증

- Windows Release build: 경고 0, 오류 0
- .NET 전체 테스트: 51개 통과, 실패/건너뜀 0
- Android 공통 코드 단위/Robolectric 테스트: 10개 통과, 실패/건너뜀 0
- Android assembleRelease, lintRelease, lintDebug 성공. Lint 비차단 경고는 남아 있으며 경고 0으로 보고하지 않습니다.
- 서명 APK의 apksigner 검증 성공, Windows self-contained 설치 EXE 생성

Android Robolectric은 파일 기반 DocumentFile 어댑터이며 실제 휴대폰 SAF 공급자가 아닙니다. WPF 테스트는 창을 표시하지 않는 레이아웃 검사이며 시각/클릭 QA가 아닙니다.

## 설치 및 주의사항

Windows는 MeshDriveSetup.exe, Android는 MeshDrive-release.apk를 사용합니다. 자세한 안내는 [README](https://github.com/esleeeeee/eslee-meshdrive/blob/main/README.md)를 참고하세요. Windows EXE는 Authenticode 미서명입니다. Android debug 앱과 release 앱은 서명이 달라 직접 덮어 업데이트되지 않습니다.

자동 동기화 규칙은 Windows Agent가 관리합니다. Android 앱 재부팅 자동 시작이나 Android끼리 독립 예약 동기화는 제공하지 않습니다. Android 제조사 절전·폴더 공급자 동작은 실기기에서 확인해야 합니다.

이전 버전은 앱 데이터에 저장됩니다. 앱 데이터 삭제는 이 복구 자료도 삭제하며 외부 백업을 대신하지 않습니다. 최초 동기화는 테스트 폴더로 검증하세요.

[사용자가 해야 하는 최종 검증 목록](https://github.com/esleeeeee/eslee-meshdrive/blob/main/docs/MANUAL-VERIFICATION.md): PC↔PC/PC↔Android, 플레이어 seek, 대용량 중단/재개, 3기기 복사, 동기화 충돌/복원, 설치·업데이트·절전. 통과 전에는 실사용 최종 합격으로 표시하지 않습니다.
