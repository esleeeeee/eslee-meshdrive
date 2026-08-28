# 변경 기록

## 0.0.3 — Phase 2

- 설치마다 ECDSA 장치 인증서를 만들고, 개인키는 Windows 사용자 DPAPI로 보호합니다.
- 발견된 기기와 6자리 SAS 페어링을 합니다. 양쪽이 모두 승인한 뒤에만 상대 인증서를 신뢰합니다.
- 페어링된 Agent만 HTTPS 보호 API를 사용할 수 있습니다. 미페어링·다른 인증서·연결 해제 뒤에는 거부합니다.
- mDNS 패킷을 보낸 IPv4를 HTTPS 연결 주소로 우선하고, 광고된 다른 주소는 fallback입니다.

## 0.0.2 — Phase 1

- Agent가 `_meshdrive._tcp.local` mDNS 서비스를 광고하고 같은 LAN의 MeshDrive를 찾습니다.
- 설치마다 고유 Device ID를 만들고, 기기 이름은 Windows 컴퓨터 이름을 씁니다.
- Ethernet과 Wi-Fi를 함께 사용해 같은 공유기의 유선 PC와 무선 PC가 서로를 찾을 수 있게 합니다.
- 일정 시간 응답이 없으면 해당 기기를 오프라인으로 표시합니다.
- GUI가 Named Pipe로 발견된 기기 목록을 조회하고, 자기 자신은 목록에서 빼 둡니다.

## 0.0.1 — Phase 0

- Windows GUI(`MeshDrive.Windows`)와 Agent(`MeshDrive.Agent`)를 별도 프로세스로 실행합니다.
- 두 프로세스는 현재 사용자 전용 Named Pipe로 통신합니다.
- GUI에서 Agent 상태(프로세스 ID, 시작 시각, 업타임, 세션)를 확인합니다.
- 창을 닫아도 Agent는 유지되고, GUI를 다시 실행하면 같은 Agent에 재연결합니다.
- `MeshDrive 종료`에서만 Agent까지 종료합니다.
