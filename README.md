# ChzzkDownloader

치지직(NAVER CHZZK) 다시보기 URL에서 **실제 MP4 주소를 찾아** 선택한 구간을 FFmpeg로 다운로드하는 **.NET Framework 4.8 / Windows Forms** 프로그램입니다.

## 주요 기능

- `https://chzzk.naver.com/video/15707690` 또는 영상 번호 `15707690` 입력
- `https://api.chzzk.naver.com/service/v2/videos/{videoNo}` JSON 응답에서 MP4 다운로드 URL 우선 추출
- 직접 MP4 링크가 없다면 `videoId`와 `inKey`를 사용해 Naver Playback API에서 주소 조회
- MP4가 없고 라이브 다시보기의 HLS URL만 있다면 FFmpeg로 HLS 저장
- 화질 선택 (확인 가능한 해상도가 여러 개면 선택), 영상 제목·채널·길이 표시
- 기본 시작 `00:00:00`, 기본 종료 **영상 전체 길이**, `HH:MM:SS` 형식 수정 가능
- FFmpeg `-ss` / `-to` / `-c copy` 로 재인코딩 없이 구간 저장
- 실행파일과 동일한 위치의 `output` 폴더에 자동 저장 (다른 경로 지정 불가)
- 실행파일 옆 `ffmpeg.exe`만 사용 (파일 선택 및 PATH 검색 없음)
- 파일명 변경, 진행률, 취소 및 오류 로그
- 파일 검증 완료 후에만 다운로드 완료 표시
- 다운로드 직전에 실제 MP4 주소를 다시 조회해 만료된 URL 사용 가능성 감소

## 개발 환경 및 실행

1. **Visual Studio 2022** (또는 .NET Framework 4.8 타겟 팩이 설치된 Visual Studio)에서 `ChzzkDownloader.sln`을 엽니다.
2. **.NET Framework 4.8 Developer Pack** 및 **.NET 데스크톱 개발** 구성 요소가 필요합니다.
3. 솔루션을 빌드합니다 (`Release | Any CPU`).
4. [FFmpeg 공식 사이트](https://ffmpeg.org/download.html)에서 Windows용 `ffmpeg.exe`를 준비합니다.
5. **반드시** 실행 파일(`ChzzkDownloader.exe`)과 같은 폴더에 `ffmpeg.exe`를 놓습니다.
6. 실행 시 비어 있는 링크 입력란에 VOD 주소를 넣고 → **영상 정보 조회** → 시간/화질 조정 → **다운로드 시작**.
7. 실행 파일 옆에 `output` 폴더가 자동 생성되고, 검증된 MP4만 최종 파일명으로 저장됩니다.

**별도 NuGet 패키지는 필요하지 않습니다.** FFmpeg 바이너리는 저장소에 포함되지 않습니다.

## FFmpeg 명령 예시

다음과 같은 입력측 시킹을 사용하며 옵션을 시스템 프로세스로 전달합니다 (셸 명령 문자열을 실행하지 않습니다).

```powershell
ffmpeg.exe -ss 07:37:57 -to 07:40:11 -i "https://.../video.mp4?...signed..." -c copy filename.mp4
```

실제 실행 시에는 진행률용 `-progress pipe:1`, 임시 파일, `-movflags +faststart` 등의 옵션이 추가됩니다. `-c copy` 구간 자르기는 비디오 키프레임 위치에 따라 요청한 시작점과 약간 다를 수 있습니다.

## API 추출 방식

치지직 영상 페이지 주소의 `/video/{videoNo}` 부분을 추출합니다. **페이지의 숫자 ID(`videoNo`)와 Playback API의 내부 `videoId`는 서로 다른 값일 수 있습니다.** 먼저

```http
GET https://api.chzzk.naver.com/service/v2/videos/15707690
```

을 호출해 제목/영상 길이와 JSON의 MP4 URL을 확인합니다. MP4 URL이 직접 포함되어 있으면 해당 링크를 사용합니다. 없다면 필요할 때만 아래 경로를 추가 호출합니다.

```http
GET https://apis.naver.com/neonplayer/vodplay/v2/playback/{videoId}?key={inKey}
```

여기서 `period[].adaptationSet[].representation[].baseURL[0].value`가 MP4 링크인 항목을 추출합니다.

이 경로들은 **비공식 API**이며 치지직 변경, 지역/연령 제한, 비공개/삭제된 영상, 링크 만료, CDN의 다운로드 속도 제한 등에 따라 동작하지 않을 수 있습니다. 로그인 쿠키/인증 우회 기능은 제공하지 않습니다. 본인이 다운로드할 권한이 있는 영상에만 사용하세요.

## 기술적 참고사항

- 종료 시간은 영상 조회 시 확인한 전체 길이로 자동 설정됩니다.
- 다운로드 시 기존 대상 파일이 있으면 덮어쓰기 전에 확인합니다.
- 진행 중 파일은 `*.partial.mp4`로 작성하고 **성공 시에만** 지정된 이름으로 이동합니다.
- FFmpeg 종료 코드가 정상이어도 생성된 MP4를 다시 열어 실제 영상 스트림·길이·패킷을 검사합니다. 검증에 실패하면 **완료 대신 오류**를 표시합니다.
- 진행률 99%는 저장 완료가 아니며 검증 성공 후에만 100%로 표시됩니다.
- 작업이 실패하거나 취소되면 임시 파일 삭제를 시도합니다.
- 파일명에 쓸 수 없는 Windows 문자는 `_`로 바꿉니다.
- FFmpeg 실행 파일은 실행파일과 같은 폴더의 `ffmpeg.exe`로 고정됩니다.
- Windows 플랫폼의 Visual Studio 환경이 없어 현재 코드는 CI에서 빌드를 검증하도록 설정했습니다.

## 배포 폴더 예시

```text
Release/
  ChzzkDownloader.exe
  ffmpeg.exe
  output/                  # 다운로드 시 자동 생성
    VOD_제목.mp4
```

## 프로젝트 구성

```text
ChzzkDownloader.sln
ChzzkDownloader/
  ChzzkDownloader.csproj
  Program.cs
  MainForm.cs             WinForms UI
  ChzzkClient.cs          CHZZK API / MP4 링크 조회
  FfmpegDownloader.cs     FFmpeg 실행 및 진행률 / 취소
  TimeCode.cs             HH:MM:SS 변환 및 검증
```
