# 변경 기록

## 0.0.1 — Phase 0

- Windows GUI(`MeshDrive.Windows`)와 Agent(`MeshDrive.Agent`)를 별도 프로세스로 실행합니다.
- 두 프로세스는 현재 사용자 전용 Named Pipe로 통신합니다.
- GUI에서 Agent 상태(프로세스 ID, 시작 시각, 업타임, 세션)를 확인합니다.
- 창을 닫아도 Agent는 유지되고, GUI를 다시 실행하면 같은 Agent에 재연결합니다.
- `MeshDrive 종료`에서만 Agent까지 종료합니다.
