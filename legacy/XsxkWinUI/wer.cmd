@echo off
wevtutil qe Application /q:"*[System[Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting']]]" /c:4 /rd:true /f:text
