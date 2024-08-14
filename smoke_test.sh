#!/bin/sh
set -e

rm -rf test
mkdir test
python -m zipfile -e testdata/MAA-v5.3.2-alpha.1.d013.g1652647f1-win-x64.zip test/long_package &
python -m zipfile -e testdata/MAA-v5.4.1-alpha.1.d090.g73458d38c-win-x64.zip test/short_package &
python -m zipfile -e testdata/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64.zip test/ref_package &
wait

echo "case 1: v5.3.2-alpha.1.d013.g1652647f1 to v5.4.2-alpha.1.d104.g2428a4610"
echo '{"name":"MAA","version":"v5.3.2-alpha.1.d013.g1652647f1","variant":"win-x64"}' > test/smoke_test_input_long.json
dotnet run --project MaaUpdateEngine.SmokeTest -- test/long_workdir test/smoke_test_input_long.json test/long_package file://./output/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64-delta.tar.zst
echo -n "DIFF test/long_package test/ref_package: "
diff -ur test/long_package test/ref_package
echo "PASS"

echo "case 2: v5.4.1-alpha.1.d090.g73458d38c to v5.4.2-alpha.1.d104.g2428a4610"
echo '{"name":"MAA","version":"v5.4.1-alpha.1.d090.g73458d38c","variant":"win-x64"}' > test/smoke_test_input_short.json
dotnet run --project MaaUpdateEngine.SmokeTest -- test/short_workdir test/smoke_test_input_short.json test/short_package file://./output/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64-delta.tar.zst
echo -n "DIFF test/short_package test/ref_package: "
diff -ur test/short_package test/ref_package
echo "PASS"

# TODO: smoke test for all supported versions (preferably not shell)

echo "case 3: v5.4.1-alpha.1.d090.g73458d38c to v5.4.2-alpha.1.d104.g2428a4610 with running exe"
python -m zipfile -e testdata/MAA-v5.4.1-alpha.1.d090.g73458d38c-win-x64.zip test/running_package
test/running_package/MAA.exe &
sleep 1
pid=$!
echo '{"name":"MAA","version":"v5.4.1-alpha.1.d090.g73458d38c","variant":"win-x64"}' > test/smoke_test_input_short.json
dotnet run --project MaaUpdateEngine.SmokeTest -- test/running_workdir test/smoke_test_input_short.json test/running_package file://./output/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64-delta.tar.zst
kill $pid
echo -n "DIFF test/running_package test/ref_package: "
diff -ur test/running_package test/ref_package -x cache -x config -x debug
echo "PASS"
