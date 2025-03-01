//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class CompositeBlockPreprocessorStep : IBlockPreprocessorStep
    {
        private readonly LinkedList<IBlockPreprocessorStep> _recoverySteps;

        public CompositeBlockPreprocessorStep(params IBlockPreprocessorStep[] recoverySteps)
        {
            if (recoverySteps == null) throw new ArgumentNullException(nameof(recoverySteps));
            
            _recoverySteps = new LinkedList<IBlockPreprocessorStep>();
            for (int i = 0; i < recoverySteps.Length; i++)
            {
                _recoverySteps.AddLast(recoverySteps[i]);
            }
        }
        
        public void RecoverData(Block block)
        {
            foreach (IBlockPreprocessorStep recoveryStep in _recoverySteps)
            {
                recoveryStep.RecoverData(block);
            }
        }
        
        public void AddFirst(IBlockPreprocessorStep blockPreprocessorStep)
        {
            _recoverySteps.AddFirst(blockPreprocessorStep);
        }
        
        public void AddLast(IBlockPreprocessorStep blockPreprocessorStep)
        {
            _recoverySteps.AddLast(blockPreprocessorStep);
        }
    }
}
