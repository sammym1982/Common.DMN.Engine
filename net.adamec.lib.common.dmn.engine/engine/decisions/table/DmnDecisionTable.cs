﻿using System;
using System.Collections.Generic;
using System.Linq;
using net.adamec.lib.common.dmn.engine.engine.definition;
using net.adamec.lib.common.dmn.engine.engine.runtime;
using net.adamec.lib.common.dmn.engine.parser.dto;
using net.adamec.lib.common.logging;

namespace net.adamec.lib.common.dmn.engine.engine.decisions.table
{
    /// <summary>
    /// DMN Decision Table definition
    /// </summary>
    public class DmnDecisionTable : DmnDecision
    {
        /// <summary>
        /// Decision table hit policy
        /// </summary>
        public HitPolicyEnum HitPolicy { get; }
        /// <summary>
        /// Aggregation type of collect hit policy
        /// </summary>
        public CollectHitPolicyAggregationEnum Aggregation { get; }

        /// <summary>
        /// Decision table outputs
        /// </summary>
        public List<DmnDecisionTableOutput> Outputs { get; }
        /// <summary>
        /// Decision table inputs
        /// </summary>
        public List<DmnDecisionTableInput> Inputs { get; }
        /// <summary>
        /// Decision table rules
        /// </summary>
        public List<DmnDecisionTableRule> Rules { get; }


        public DmnDecisionTable(
            string name,
            HitPolicyEnum hitPolicy, CollectHitPolicyAggregationEnum aggregation,
            List<DmnDecisionTableInput> inputs,
            List<DmnDecisionTableOutput> outputs,
            List<DmnDecisionTableRule> rules,
            List<DmnVariableDefinition> requiredInputs, List<IDmnDecision> requiredDecisions)
            : base(name, requiredInputs, requiredDecisions)
        {
            HitPolicy = hitPolicy;
            Aggregation = aggregation;
            Inputs = inputs;
            Outputs = outputs;
            Rules = rules;
        }

        /// <summary>
        /// Evaluates the decision table.
        /// </summary>
        /// <remarks>
        /// While evaluating the decision table, the <seealso cref="Rules"/> are evaluated first.
        /// Then it evaluates (calculates) the outputs for positive rules and applies the hit policy.
        /// </remarks>
        /// <param name="context">DMN Engine execution context</param>
        /// <param name="correlationId">Optional correlation ID used while logging</param>
        /// <returns>Decision result</returns>
        public override DmnDecisionResult Evaluate(DmnExecutionContext context, string correlationId = null)
        {
            //EVALUATE RULES
            var positiveRules = EvaluateRules(context, correlationId);

            //EVALUATE OUTPUTS for positive rules
            var results = EvaluateOutputsForPositiveRules(context, correlationId, positiveRules);

            //APPLY HIT POLICY
            if (positiveRules.Count <= 0) return new DmnDecisionResult();

            var retVal = new DmnDecisionResult();
            var positiveMatchOutputRules = new List<DmnDecisionTableRule>();
            // ReSharper disable once SwitchStatementMissingSomeCases
            switch (HitPolicy)
            {
                case HitPolicyEnum.Unique:
                    // No overlap is possible and all rules are disjoint. Only a single rule can be matched
                    // Overlapping rules represent an error.
                    if (positiveRules.Count > 1)
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"UNIQUE hit policy violation - {positiveRules.Count} matches");
                    positiveMatchOutputRules.Add(positiveRules[0]);
                    break;
                case HitPolicyEnum.First:
                    //Multiple(overlapping) rules can match, with different output entries. The first hit by rule order is returned
                    positiveMatchOutputRules.Add(positiveRules[0]);
                    break;
                case HitPolicyEnum.Priority:
                    // Multiple rules can match, with different output entries. This policy returns the matching rule with the highest output priority.
                    //  Output priorities are specified in the ordered list of output values, in decreasing order of priority. Note that priorities are independent from rule sequence
                    // A P table that omits allowed output values is an error.
                    var orderedPositiveRules = OrderPositiveRulesByOutputPriority(positiveRules, results, correlationId);
                    if (orderedPositiveRules == null)
                    {
                        //A P table that omits allowed output values is an error.
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"PRIORITY hit policy violation - no outputs with Allowed Values");
                    }
                    positiveMatchOutputRules.Add(orderedPositiveRules.ToArray()[0]);
                    break;
                case HitPolicyEnum.Any:
                    //  There may be overlap, but all of the matching rules show equal output entries for each output, so any match can be used.
                    //  If the output entries are non-equal, the hit policy is incorrect and the result is an error.
                    if (!MatchRuleOutputs(positiveRules, results))
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"ANY hit policy violation - the outputs don't match");
                    positiveMatchOutputRules.Add(positiveRules[0]);
                    break;


                case HitPolicyEnum.Collect:
                    // Multiple rules can be satisfied. The decision table result contains the output of all satisfied rules in an arbitrary order as a list.
                    // If an aggregator is specified, the decision table result will only contain a single output entry. The aggregator will generate the output entry from all satisfied rules.
                    // Except for C-count and C-list, the rules must have numeric output values (bool is also allowed - 0=false, 1=true).    
                    // If the Collect hit policy is used with an aggregator, the decision table can only have one output.
                    if (Outputs.Count > 1 && Aggregation != CollectHitPolicyAggregationEnum.List)
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"COLLECT hit policy violation - multiple outputs not allowed for aggregation {Aggregation}");

                    var outType = Outputs[0].Variable.Type;
                    var outTypeIsNumeric = outType == typeof(int) || outType == typeof(long) || outType == typeof(double);
                    var outTypeIsBool = outType == typeof(bool);
                    var outTypeIsStr = outType == typeof(string);

                    var isAllowedForCount = outTypeIsNumeric || outTypeIsStr;
                    var isAllowedForSumMinMax = outTypeIsNumeric || outTypeIsBool;

                    if (!
                        (Aggregation == CollectHitPolicyAggregationEnum.List ||
                        (Aggregation == CollectHitPolicyAggregationEnum.Count && isAllowedForCount) ||
                         Aggregation != CollectHitPolicyAggregationEnum.List && Aggregation != CollectHitPolicyAggregationEnum.Count && isAllowedForSumMinMax))
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"COLLECT hit policy violation - type {outType.Name} not allowed for aggregation {Aggregation}");


                    if (Aggregation == CollectHitPolicyAggregationEnum.List)
                    {
                        positiveMatchOutputRules.AddRange(positiveRules);
                    }
                    else
                    {
                        var collectValues = isAllowedForSumMinMax ?
                            CalculatePositiveRulesCollectValues(positiveRules, results) :
                            CalculatePositiveRulesCollectCountValue(positiveRules, results);

                        var output = Outputs[0];
                        var outputVariable = context.GetVariable(output.Variable);
                        if (collectValues.Count == 0 && Aggregation != CollectHitPolicyAggregationEnum.Count)
                        {
                            //no value for sum, min, max
                            outputVariable.Value = null;
                            retVal += new DmnDecisionSingleResult() + outputVariable.Clone();
                            return retVal;
                        }
                        // ReSharper disable once SwitchStatementMissingSomeCases
                        switch (Aggregation)
                        {
                            case CollectHitPolicyAggregationEnum.Sum:
                                outputVariable.Value = collectValues.Sum;
                                break;
                            case CollectHitPolicyAggregationEnum.Min:
                                outputVariable.Value = collectValues.Min;
                                break;
                            case CollectHitPolicyAggregationEnum.Max:
                                outputVariable.Value = collectValues.Max;
                                break;
                            case CollectHitPolicyAggregationEnum.Count:
                                outputVariable.Value = collectValues.Count;
                                break;
                        }

                        retVal += new DmnDecisionSingleResult() + outputVariable.Clone();
                        return retVal;

                    }

                    break;
                case HitPolicyEnum.RuleOrder:
                    //Multiple rules can be satisfied.The decision table result contains the output of all satisfied rules in the order of the rules in the decision table.
                    positiveMatchOutputRules.AddRange(positiveRules);

                    break;
                case HitPolicyEnum.OutputOrder:
                    //Returns all hits in decreasing output priority order. 
                    // Output priorities are specified in the ordered list of output values in decreasing order of priority
                    var orderedPositiveRules2 = OrderPositiveRulesByOutputPriority(positiveRules, results, correlationId);
                    if (orderedPositiveRules2 == null)
                    {
                        //A P table that omits allowed output values is an error.
                        throw Logger.Error<DmnExecutorException>(
                            correlationId,
                            $"OUTPUT ORDER hit policy violation - no outputs with Allowed Values");
                    }
                    positiveMatchOutputRules.AddRange(orderedPositiveRules2);
                    break;
            }

            //Return (multiple) matches
            foreach (var multiMatchOutputRule in positiveMatchOutputRules)
            {
                var singleResult = new DmnDecisionSingleResult();
                foreach (var ruleOutput in multiMatchOutputRule.Outputs)
                {
                    var resultOutput = results.GetResult(multiMatchOutputRule, ruleOutput);
                    if (resultOutput == null) continue;

                    context.GetVariable(ruleOutput.Output.Variable).Value = resultOutput.Value;
                    singleResult += resultOutput;
                }

                retVal += singleResult;
            }

            return retVal;
        }

        private List<DmnDecisionTableRule> EvaluateRules(DmnExecutionContext context, string correlationId)
        {
            var positiveRules = new List<DmnDecisionTableRule>();

            //EVALUATE RULES
            Logger.Info(correlationId, message: $"Evaluating decision table {Name} rules...");
            foreach (var rule in Rules)
            {
                var match = true;
                foreach (var ruleInput in rule.Inputs)
                {
                    //check allowed input values
                    var allowedValues = ruleInput.Input.AllowedValues;
                    if (allowedValues != null && allowedValues.Count > 0)
                    {
                        string value = null;
                        if (!string.IsNullOrEmpty(ruleInput.Input.Expression))
                        {
                            //input is mapped to expression, so evaluate it to ger the value
                            var valueObj = context.EvalExpression<object>(ruleInput.Input.Expression);
                            if (valueObj != null) value = valueObj.ToString();
                        }
                        else
                        {
                            //input is mapped to variable
                            value = context.GetVariable(ruleInput.Input.Variable).Value?.ToString();
                        }
                        if (!allowedValues.Contains(value))
                            throw Logger.Error<DmnExecutorException>(
                                correlationId,
                                $"Decision table {Name}, rule #{rule.Index}: Input value '{value}' is not in allowed values list ({string.Join(",", allowedValues)})");
                    }
                    if (Logger.IsTraceEnabled)
                        Logger.Trace(correlationId, message: $"Evaluating decision table {Name} rule {rule} input #{ruleInput.Input.Index}: {ruleInput.Expression}... ");
                    var result =
                        context.EvalExpression<bool>(ruleInput.Expression); //TODO ?pre-parse and use the inputs as parameters?
                    if (Logger.IsTraceEnabled)
                        Logger.Trace(correlationId, message: $"Evaluated decision table {Name} rule {rule} input #{ruleInput.Input.Index}: {ruleInput.Expression} with result {result}");
                    if (!result)
                    {
                        match = false;
                        break;
                    }
                }

                Logger.Info(correlationId,
                    message: $"Evaluated decision table {Name} rule: {(match ? "POSITIVE" : "NEGATIVE")} - {rule}");
                if (match)
                {
                    positiveRules.Add(rule);
                }
            }

            Logger.Info(correlationId, message: $"Evaluated decision table {Name} rules");
            return positiveRules;
        }

        private DmnDecisionTableRuleExecutionResults EvaluateOutputsForPositiveRules(DmnExecutionContext context, string correlationId, IEnumerable<DmnDecisionTableRule> positiveRules)
        {
            Logger.Info(correlationId, message: $"Evaluating decision table {Name} positive rules outputs...");
            var results = new DmnDecisionTableRuleExecutionResults();
            foreach (var positiveRule in positiveRules)
            {
                //outputs
                foreach (var ruleOutput in positiveRule.Outputs)
                {
                    if (string.IsNullOrEmpty(ruleOutput.Expression))
                    {
                        results.SetResult(positiveRule, ruleOutput, null);
                        continue;
                    }

                    var result = context.EvalExpression(ruleOutput.Expression, ruleOutput.Output.Variable.Type ?? typeof(object));

                    // check allowed output values
                    var allowedValues = ruleOutput.Output.AllowedValues;
                    if (allowedValues != null && allowedValues.Count > 0 &&
                        !string.IsNullOrEmpty(result?.ToString()))
                    {
                        if (!ruleOutput.Output.AllowedValues.Contains(result.ToString()))
                        {
                            throw Logger.Error<DmnExecutorException>(
                                correlationId,
                                $"Decision table {Name}, rule {positiveRule}: Output value '{result}' is not in allowed values list ({string.Join(",", allowedValues)})");
                        }
                    }

                    var output = new DmnExecutionVariable(ruleOutput.Output.Variable) { Value = result };
                    results.SetResult(positiveRule, ruleOutput, output);
                    if (Logger.IsTraceEnabled)
                        Logger.Trace(correlationId,
                            message: $"Positive decision table {Name} rule {positiveRule}: output {output.Name}, value {output.Value}");
                }
            }

            Logger.Info(correlationId, message: $"Evaluated decision table {Name} positive rules outputs");
            return results;
        }

        private static bool MatchRuleOutputs(IEnumerable<DmnDecisionTableRule> positiveRules, DmnDecisionTableRuleExecutionResults results)
        {

            var arry = positiveRules.ToArray();
            if (arry.Length < 2) return true;

            var firstItem = results.GetResultsHashCode(arry[0]);
            var allEqual = arry.Skip(1).All(i => Equals(firstItem, results.GetResultsHashCode(i)));
            return allEqual;
        }

        private IOrderedEnumerable<DmnDecisionTableRule> OrderPositiveRulesByOutputPriority(
            IReadOnlyCollection<DmnDecisionTableRule> positiveRules,
            DmnDecisionTableRuleExecutionResults results,
            string correlationId)
        {
            IOrderedEnumerable<DmnDecisionTableRule> ordered = null;
            foreach (var output in Outputs)
            {
                if (output.AllowedValues == null || output.AllowedValues.Count <= 0) continue;

                if (ordered == null)
                {
                    ordered = positiveRules.OrderBy(i =>
                    {
                        var ruleOutput = i.Outputs.FirstOrDefault(o => o.Output.Index == output.Index);

                        //no such output in positive rule (ruleOutput==null) - at the end of the list
                        //no output result in positive rule (ruleOutput.ResultOutput==null) - at the end of the list
                        //no output result value in positive rule (ruleOutput.ResultOutput.Value==null) - at the end of the list 
                        //all handled in GetIndexOfOutputValue: if (value == null) return int.MaxValue;
                        return GetIndexOfOutputValue(output.AllowedValues, results.GetResult(i, ruleOutput)?.Value, correlationId);
                    });
                }
                else
                {
                    ordered = ordered.ThenBy(i =>
                    {
                        var ruleOutput = i.Outputs.FirstOrDefault(o => o.Output.Index == output.Index);
                        return GetIndexOfOutputValue(output.AllowedValues, results.GetResult(i, ruleOutput)?.Value, correlationId);
                    });
                }
            }

            return ordered;
        }

        private int GetIndexOfOutputValue(IList<string> allowedValues, object value, string correlationId)
        {
            //null value at the end
            if (value == null) return int.MaxValue;
            var strVal = value.ToString().Trim();
            var idx = allowedValues.IndexOf(strVal);
            //not found - exception, but shoul not happen
            if (idx == -1) throw Logger.Fatal<DmnExecutorException>(
                correlationId,
                $"GetIndexOfOutputValue: Output value '{value}' is not in allowed values list ({string.Join(",", allowedValues)})");

            return idx;
        }

        private static PositiveRulesCollectValues CalculatePositiveRulesCollectValues(
            IEnumerable<DmnDecisionTableRule> positiveRules,
            DmnDecisionTableRuleExecutionResults results)
        {
            double sum = 0;
            var min = double.MaxValue;
            var max = double.MinValue;

            var distinctValues = new List<double>();

            foreach (var positiveRule in positiveRules)
            {
                var valueRaw = results.GetResult(positiveRule, positiveRule.Outputs[0])?.Value;
                if (valueRaw == null) continue; //ignore null results/values

                double value;
                var isBool = positiveRule.Outputs[0].Output.Variable.Type == typeof(bool);
                if (isBool)
                {
                    value = (bool)valueRaw ? 1 : 0;
                }
                else
                {
                    value = (double)Convert.ChangeType(valueRaw, typeof(double));
                }

                sum += value;
                if (value < min) min = value;
                if (value > max) max = value;
                if (!distinctValues.Contains(value))
                {
                    distinctValues.Add(value);
                }
            }

            var count = distinctValues.Count;
            return new PositiveRulesCollectValues(sum, min, max, count);
        }

        private static PositiveRulesCollectValues CalculatePositiveRulesCollectCountValue(
            IEnumerable<DmnDecisionTableRule> positiveRules,
            DmnDecisionTableRuleExecutionResults results)
        {
           var count = positiveRules.ToList()
                .Select(r => results.GetResult(r, r.Outputs[0])?.Value?.ToString())
                .Where(v => v != null)
                .Distinct()
                .ToList()
                .Count;
            
            return new PositiveRulesCollectValues(0, 0, 0, count);
        }

        /// <summary>
        /// Container of aggregate values for positive rules when <see cref="HitPolicyEnum.Collect"/> hit policy is used.
        /// </summary>
        private class PositiveRulesCollectValues
        {
            /// <summary>
            /// Sum of evaluated values
            /// </summary>
            public readonly double Sum;
            /// <summary>
            /// Min evaluated value
            /// </summary>
            public readonly double Min;
            /// <summary>
            /// Max evaluated value
            /// </summary>
            public readonly double Max;
            /// <summary>
            /// Count of distinct evaluated values
            /// </summary>
            public readonly int Count;

            /// <summary>
            /// CTOR
            /// </summary>
            /// <param name="sum">Sum of evaluated values</param>
            /// <param name="min">Min evaluated value</param>
            /// <param name="max">Max evaluated value</param>
            /// <param name="count">Count of distinct evaluated values</param>
            public PositiveRulesCollectValues(double sum, double min, double max, int count)
            {
                Sum = sum;
                Min = min;
                Max = max;
                Count = count;
            }
        }
    }
}

