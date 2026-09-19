#!/usr/bin/env bash
# CvHub business-logic test suite. Runs on the VPS against the deployed app.
# Verifies business RULES: access filters, publish gating, project caps,
# tag preselection, CV privacy, comment anonymity, lockout behavior.
set -u
APP=http://localhost:5001
RUN=$(date +%s)

tok() { grep -o 'name="__RequestVerificationToken" value="[^"]*"' "$1" | head -1 | sed 's/.*value="//;s/"//'; }

login() { # email password jar
  local jar="$3"; rm -f "$jar"
  curl -s -c "$jar" $APP/Account/Login -o /tmp/lt.html
  local T=$(tok /tmp/lt.html)
  curl -s -b "$jar" -c "$jar" -o /dev/null -X POST $APP/Account/Login \
    --data-urlencode '_handler=login' --data-urlencode "__RequestVerificationToken=$T" \
    --data-urlencode "Input.Email=$1" --data-urlencode "Input.Password=$2"
}

PASS=0; FAIL=0
check() { if [ "$2" == "$3" ]; then PASS=$((PASS+1)); echo "PASS: $1 ($2)"; else FAIL=$((FAIL+1)); echo "FAIL: $1 (got '$2', want '$3')"; fi; }
contains() { if echo "$2" | grep -q "$3"; then PASS=$((PASS+1)); echo "PASS: $1"; else FAIL=$((FAIL+1)); echo "FAIL: $1 (missing '$3')"; fi; }

login recruiter@cvhub.local 'Recruiter123!' /tmp/T_rec.txt
login candidate2@cvhub.local 'Candidate123!' /tmp/T_cand.txt
login candidate@cvhub.local 'Candidate123!' /tmp/T_cand1.txt
check "recruiter session" "$(curl -s -b /tmp/T_rec.txt -o /dev/null -w '%{http_code}' $APP/api/positions/list)" "200"
check "candidate2 session" "$(curl -s -b /tmp/T_cand.txt -o /dev/null -w '%{http_code}' $APP/api/profile/attributes/1)" "200"

echo "--- fixtures (run $RUN) ---"
mkattr() { curl -s -b /tmp/T_rec.txt -X POST $APP/api/attributes -H 'Content-Type: application/json' -d "$1" | python3 -c "import json,sys; print(json.load(sys.stdin).get('id',0))"; }
NUM=$(mkattr "{\"Name\":\"LT$RUN Years\",\"Category\":\"Experience\",\"Description\":null,\"Type\":4,\"Options\":null,\"Version\":null}")
BOOL=$(mkattr "{\"Name\":\"LT$RUN Remote\",\"Category\":\"Other\",\"Description\":null,\"Type\":7,\"Options\":null,\"Version\":null}")
OM=$(mkattr "{\"Name\":\"LT$RUN Level\",\"Category\":\"Other\",\"Description\":null,\"Type\":8,\"Options\":\"Junior\\nMid\\nSenior\",\"Version\":null}")
MISS=$(mkattr "{\"Name\":\"LT$RUN Req\",\"Category\":\"Other\",\"Description\":null,\"Type\":1,\"Options\":null,\"Version\":null}")
NUM2=$(mkattr "{\"Name\":\"LT$RUN Unused\",\"Category\":\"Other\",\"Description\":null,\"Type\":4,\"Options\":null,\"Version\":null}")
echo "attr ids: NUM=$NUM BOOL=$BOOL OM=$OM MISS=$MISS NUM2=$NUM2"
for v in "$NUM" "$BOOL" "$OM" "$MISS" "$NUM2"; do
  if ! echo "$v" | grep -qE '^[0-9]+$' || [ "$v" = "0" ]; then echo "FIXTURE FAILURE (attr id '$v') — aborting"; exit 1; fi
done

setval() { # attrId field jsonValue   (field: numericValue|boolValue|optionValue|stringValue|none)
  local ver=$(curl -s -b /tmp/T_cand.txt $APP/api/profile/attributes/$1 | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['version'] if d['version'] is not None else 0)")
  local body=$(python3 - "$1" "$2" "$3" "$ver" <<'PY'
import json, sys
aid, field, value, ver = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
body = {"userId": None, "version": int(ver) if ver != "0" else None,
        "stringValue": None, "textValue": None, "imageUrl": None,
        "numericValue": None, "dateValue": None, "periodStart": None,
        "periodEnd": None, "boolValue": False, "optionValue": None}
if field == "none":
    body["stringValue"] = ""
else:
    body[field] = json.loads(value)
print(json.dumps(body))
PY
)
  curl -s -b /tmp/T_cand.txt -o /dev/null -w '%{http_code}' -X POST $APP/api/profile/attributes/$1 -H 'Content-Type: application/json' -d "$body"
}
mkpos() { # title access maxproj attrsjson filtersjson tagsjson -> id
  curl -s -b /tmp/T_rec.txt -X POST $APP/api/positions -H 'Content-Type: application/json' \
    -d "{\"Title\":\"$1\",\"ShortDescription\":\"t\",\"Company\":\"T\",\"Level\":\"Mid\",\"Access\":$2,\"MaxProjects\":$3,\"Attributes\":$4,\"Filters\":$5,\"TagIds\":$6,\"Version\":null}" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('id',0))"
}
trycv() { curl -s -b /tmp/T_cand.txt -o /tmp/cv_res.txt -w '%{http_code}' -X POST $APP/api/cvs -H 'Content-Type: application/json' -d "{\"positionId\":$1,\"maxProjects\":3}"; }
delcv()  { curl -s -b /tmp/T_cand.txt -o /dev/null -X DELETE $APP/api/cvs/$1; }
delpos() { curl -s -b /tmp/T_rec.txt -o /dev/null -X DELETE $APP/api/positions/$1; }
newcvid() { python3 -c "import json; print(json.load(open('/tmp/cv_res.txt')).get('id',0))"; }

echo "=== B: AccessRules truth table ==="
B_IDS=""
# B1 numeric GreaterThan(3) 5
P=$(mkpos "LT$RUN B1" 2 5 "[]" "[{\"attributeId\":$NUM,\"operator\":3,\"value\":\"5\"}]" null); B_IDS="$B_IDS $P"
setval $NUM numericValue 3 >/dev/null; # NOTE: CV-create denials return 403 ("You do not have access"), duplicates 409.
R=$(trycv $P); check "B1 GT5 value=3 denied" "$R" "403"
setval $NUM numericValue 7 >/dev/null; R=$(trycv $P); check "B1 GT5 value=7 allowed" "$R" "200"; delcv $(newcvid)
# B2 boundaries GTE(4)/LT(5)
P=$(mkpos "LT$RUN B2a" 2 5 "[]" "[{\"attributeId\":$NUM,\"operator\":4,\"value\":\"5\"}]" null); B_IDS="$B_IDS $P"
setval $NUM numericValue 5 >/dev/null; R=$(trycv $P); check "B2 GTE5 boundary=5 allowed" "$R" "200"; delcv $(newcvid)
setval $NUM numericValue 4 >/dev/null; R=$(trycv $P); check "B2 GTE5 value=4 denied" "$R" "403"
P2=$(mkpos "LT$RUN B2b" 2 5 "[]" "[{\"attributeId\":$NUM,\"operator\":5,\"value\":\"5\"}]" null); B_IDS="$B_IDS $P2"
setval $NUM numericValue 5 >/dev/null; R=$(trycv $P2); check "B2 LT5 boundary=5 denied" "$R" "403"
# B3 boolean IsTrue(7)
P=$(mkpos "LT$RUN B3" 2 5 "[]" "[{\"attributeId\":$BOOL,\"operator\":7,\"value\":\"\"}]" null); B_IDS="$B_IDS $P"
setval $BOOL boolValue false >/dev/null; R=$(trycv $P); check "B3 IsTrue false denied" "$R" "403"
setval $BOOL boolValue true >/dev/null; R=$(trycv $P); check "B3 IsTrue true allowed" "$R" "200"; delcv $(newcvid)
# B4 OneOfMany Equals(1)/NotEquals(2)
P=$(mkpos "LT$RUN B4a" 2 5 "[]" "[{\"attributeId\":$OM,\"operator\":1,\"value\":\"Senior\"}]" null); B_IDS="$B_IDS $P"
setval $OM optionValue '"Junior"' >/dev/null; R=$(trycv $P); check "B4 Eq Junior denied" "$R" "403"
setval $OM optionValue '"Senior"' >/dev/null; R=$(trycv $P); check "B4 Eq Senior allowed" "$R" "200"; delcv $(newcvid)
P2=$(mkpos "LT$RUN B4b" 2 5 "[]" "[{\"attributeId\":$OM,\"operator\":2,\"value\":\"Senior\"}]" null); B_IDS="$B_IDS $P2"
R=$(trycv $P2); check "B4 Neq Senior w/ Senior denied" "$R" "403"
setval $OM optionValue '"Junior"' >/dev/null; R=$(trycv $P2); check "B4 Neq Senior w/ Junior allowed" "$R" "200"; delcv $(newcvid)
# B5 AND semantics
P=$(mkpos "LT$RUN B5" 2 5 "[]" "[{\"attributeId\":$NUM,\"operator\":3,\"value\":\"5\"},{\"attributeId\":$OM,\"operator\":1,\"value\":\"Senior\"}]" null); B_IDS="$B_IDS $P"
setval $NUM numericValue 7 >/dev/null; setval $OM optionValue '"Junior"' >/dev/null
R=$(trycv $P); check "B5 AND (7,Junior) denied" "$R" "403"
setval $OM optionValue '"Senior"' >/dev/null; R=$(trycv $P); check "B5 AND (7,Senior) allowed" "$R" "200"; delcv $(newcvid)
setval $NUM numericValue 3 >/dev/null; R=$(trycv $P); check "B5 AND (3,Senior) denied" "$R" "403"
# B6 missing/empty values denied
P=$(mkpos "LT$RUN B6a" 2 5 "[]" "[{\"attributeId\":$NUM2,\"operator\":3,\"value\":\"1\"}]" null); B_IDS="$B_IDS $P"
R=$(trycv $P); check "B6 numeric never-set denied" "$R" "403"
P2=$(mkpos "LT$RUN B6b" 2 5 "[]" "[{\"attributeId\":$MISS,\"operator\":1,\"value\":\"x\"}]" null); B_IDS="$B_IDS $P2"
setval $MISS none '' >/dev/null; R=$(trycv $P2); check "B6 empty string denied" "$R" "403"

echo "=== C: publish gating, caps, tag preselection ==="
mkproj() { curl -s -b /tmp/T_cand.txt -X POST $APP/api/profile/projects -H 'Content-Type: application/json' -d "{\"id\":null,\"userId\":null,\"name\":\"LT$RUN $1\",\"start\":null,\"end\":null,\"description\":\"\",\"tags\":$2}" | python3 -c "import json,sys; print(json.load(sys.stdin).get('id',0))"; }
TAGPID=$(mkproj "Tagged" '["ltx"]')
PLAIN1=$(mkproj "Plain1" '[]')
PLAIN2=$(mkproj "Plain2" '[]')
PLAIN3=$(mkproj "Plain3" '[]')
TAGID=$(curl -s -b /tmp/T_cand.txt $APP/api/tags | python3 -c "import json,sys; print([t['id'] for t in json.load(sys.stdin) if t['name']=='ltx'][0])")
echo "projects: tagged=$TAGPID plain=$PLAIN1,$PLAIN2,$PLAIN3 tagId=$TAGID"

POS_GATE=$(mkpos "LT$RUN Gate" 1 5 "[{\"id\":0,\"attributeId\":$MISS,\"required\":true,\"section\":\"Other\"}]" "[]" null)
R=$(trycv $POS_GATE); CV1=$(newcvid)
contains "CV1 created" "$(cat /tmp/cv_res.txt)" '"id"'
R=$(curl -s -b /tmp/T_rec.txt -o /dev/null -w '%{http_code}' $APP/api/cvs/$CV1); check "D: recruiter cannot view draft CV" "$R" "403"
R=$(curl -s -b /tmp/T_cand.txt -o /tmp/pub1.txt -w '%{http_code}' -X POST $APP/api/cvs/$CV1/publish)
check "C1 publish blocked (required empty)" "$R" "409"; contains "C1 message" "$(cat /tmp/pub1.txt)" "Fill all required fields"
setval $MISS stringValue '"filled"' >/dev/null
R=$(curl -s -b /tmp/T_cand.txt -o /dev/null -w '%{http_code}' -X POST $APP/api/cvs/$CV1/publish); check "C1 publish after fill allowed" "$R" "200"
R=$(curl -s -b /tmp/T_rec.txt -o /dev/null -w '%{http_code}' $APP/api/cvs/$CV1); check "D: recruiter views published CV" "$R" "200"
R=$(curl -s -b /tmp/T_cand1.txt -o /dev/null -w '%{http_code}' $APP/api/cvs/$CV1); check "D: other candidate denied published CV" "$R" "403"
R=$(curl -s -b /tmp/T_rec.txt -o /tmp/rec-create.txt -w '%{http_code}' -X POST $APP/api/cvs -H 'Content-Type: application/json' -d "{\"positionId\":$POS_GATE,\"maxProjects\":3}")
check "D: recruiter cannot create CV" "$R" "409"; contains "D: reason" "$(cat /tmp/rec-create.txt)" "Recruiters and admins cannot create"

POS_CAP=$(mkpos "LT$RUN Cap" 1 2 "[]" "[]" null)
trycv $POS_CAP >/dev/null; CV2=$(newcvid)
curl -s -b /tmp/T_cand.txt -o /dev/null -X PUT $APP/api/cvs/$CV2/projects -H 'Content-Type: application/json' -d "{\"projectIds\":[$PLAIN1,$PLAIN2,$PLAIN3]}"
SEL=$(curl -s -b /tmp/T_cand.txt $APP/api/cvs/$CV2/projects)
check "C2 cap MaxProjects=2 enforced" "$(echo $SEL | python3 -c "import json,sys; print(len(json.load(sys.stdin)['selected']))")" "2"
POS_TAGS=$(mkpos "LT$RUN Tags" 1 5 "[]" "[]" "[$TAGID]")
trycv $POS_TAGS >/dev/null; CV3=$(newcvid)
SEL=$(curl -s -b /tmp/T_cand.txt $APP/api/cvs/$CV3/projects)
check "C3 tag preselection picks only tagged project" "$(echo $SEL | python3 -c "import json,sys; sel=json.load(sys.stdin)['selected']; print(sorted(sel)==[$TAGPID])")" "True"

echo "=== D: comments anonymity ==="
R=$(curl -s -o /dev/null -w '%{http_code}' -X POST $APP/api/positions/$POS_GATE/comments -H 'Content-Type: application/json' -d '{"body":"e2e comment"}')
check "D: anonymous comment POST 401" "$R" "401"
curl -s -b /tmp/T_cand.txt -o /dev/null -X POST $APP/api/positions/$POS_GATE/comments -H 'Content-Type: application/json' -d '{"body":"e2e comment"}'
ANON=$(curl -s -b /tmp/T_cand.txt $APP/api/positions/$POS_GATE/comments | python3 -c "import json,sys; rows=json.load(sys.stdin); print(all(r['authorId'] is None for r in rows))")
check "D: candidate view hides author ids" "$ANON" "True"
REC=$(curl -s -b /tmp/T_rec.txt $APP/api/positions/$POS_GATE/comments | python3 -c "import json,sys; rows=json.load(sys.stdin); print(all(r['authorId'] is not None for r in rows) and len(rows)>0)")
check "D: recruiter view exposes author ids" "$REC" "True"

echo "=== E: lockout behavior (lockoutOnFailure=false) ==="
LO=0
for i in $(seq 1 12); do
  rm -f /tmp/lo.txt; curl -s -c /tmp/lo.txt $APP/Account/Login -o /tmp/lo1.html
  T=$(tok /tmp/lo1.html)
  curl -s -b /tmp/lo.txt -o /tmp/lo2.html -X POST $APP/Account/Login --data-urlencode '_handler=login' --data-urlencode "__RequestVerificationToken=$T" --data-urlencode 'Input.Email=candidate2@cvhub.local' --data-urlencode 'Input.Password=WrongPassword!'
  if grep -q "Invalid login attempt" /tmp/lo2.html; then LO=$((LO+1)); fi
done
check "E: 12 bad logins all 'invalid' (no lockout by design)" "$LO" "12"
login candidate2@cvhub.local 'Candidate123!' /tmp/T_ok.txt
R=$(curl -s -b /tmp/T_ok.txt -o /dev/null -w '%{http_code}' $APP/api/profile/attributes/1); check "E: correct login still works" "$R" "200"

echo "=== cleanup ==="
delcv $CV1; delcv $CV2; delcv $CV3
for p in $POS_GATE $POS_CAP $POS_TAGS $B_IDS; do delpos $p; done
for a in $NUM $BOOL $OM $MISS $NUM2; do curl -s -b /tmp/T_rec.txt -o /dev/null -X DELETE $APP/api/attributes/$a; done
# leftovers from previous aborted run (ids 9,10)
curl -s -b /tmp/T_rec.txt -o /dev/null -X DELETE $APP/api/attributes/9
curl -s -b /tmp/T_rec.txt -o /dev/null -X DELETE $APP/api/attributes/10
curl -s -b /tmp/T_cand.txt -o /dev/null -X DELETE $APP/api/profile/projects/$TAGPID
curl -s -b /tmp/T_cand.txt -o /dev/null -X DELETE $APP/api/profile/projects/$PLAIN1
curl -s -b /tmp/T_cand.txt -o /dev/null -X DELETE $APP/api/profile/projects/$PLAIN2
curl -s -b /tmp/T_cand.txt -o /dev/null -X DELETE $APP/api/profile/projects/$PLAIN3
curl -s -b /tmp/T_cand.txt $APP/api/tags | python3 -c "
import json,sys,subprocess
for t in json.load(sys.stdin):
    if t['name'] in ('ltx','e2etest','csharp'): print('tag cleanup', t['name'])"
echo "cleanup done"
echo ""
echo "================================"
echo "RESULTS: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ] && echo "ALL LOGIC TESTS PASSED" || echo "SOME TESTS FAILED"
exit $FAIL
