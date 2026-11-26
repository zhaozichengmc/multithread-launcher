dist/ 目录只放"当前要发布的版本"，push 后会由 .github/workflows/attach-assets.yml 自动：
  1. 从 dist/LightspeedDownloader-V<版本>.exe 的文件名读出标签 V<版本>
  2. 标签不存在就用 dist/NOTES.md 作为正文创建 Release
  3. 删除同名旧附件后重新上传 exe / -full.zip / README.txt
历史版本 Release 与附件不会被替换或删除。
